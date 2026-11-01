using System.Net;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using RestaurantOrders.Data;
using RestaurantOrders.Models;
using RestaurantOrders.Services;
using RestaurantOrders.Components.Pages.Admin;
using RestaurantOrders.Components.Pages.Waiter;

int assertions = 0;
void Check(bool condition, string message) { if (!condition) throw new Exception(message); assertions++; }
using var signer = ECDsa.Create(ECCurve.NamedCurves.nistP256);
byte[] der;
do { der = signer.SignData(Encoding.UTF8.GetBytes("test-signature"), HashAlgorithmName.SHA256, DSASignatureFormat.Rfc3279DerSequence); }
while (der.Length is < 70 or > 72);
var base64 = Convert.ToBase64String(der);
var hex = Convert.ToHexString(der);
var handler = new FakeApi(base64);
var factory = new FakeHttpFactory(handler);
var viva = new VivaCloudTerminalService(factory);
BankPosTerminalSettings Pos() => new() {
    Name = "Viva test", Provider = "Viva", Protocol = "Viva Cloud", IsEnabled = true,
    TerminalId = "VIVA-TID", ProviderCode = "ECR-1", ApiKey = "fake-client", ApiSecret = "fake-secret",
    Endpoint = "", AuthEndpoint = "", ParochosNspCode = "6", ProviderId = 19, TimeoutSeconds = 30
};
var pos = Pos();
var data = new ProviderPaymentData("uid-test", "20261101120000", 1000, 240, 1240, 19, hex, OriginalSignature: base64);
var live = await viva.GetLiveTerminalsAsync(pos);
Check(live.Success && live.Terminals.Single().TerminalId == pos.TerminalId, "Viva device discovery");
var oauth = handler.Requests.First();
Check(oauth.Url == "https://demo-accounts.vivapayments.com/connect/token" && oauth.AuthScheme == "Basic", "Demo OAuth uses Basic auth");
Check(oauth.Body.Contains("grant_type=client_credentials") && oauth.Body.Contains("scope=urn%3Aviva%3Apayments%3Aecr%3Aapi"), "Viva OAuth scope and grant");
Check(handler.Requests.Last().Json!["statusId"]!.GetValue<int>() == 1 && handler.Requests.Last().AuthScheme == "Bearer", "Search uses Live status and bearer token");
handler.Requests.Clear();
var approved = await viva.ChargeAsync(pos, 12.40m, "test-ref", data);
Check(approved.Success && approved.TransactionId == "viva-txn" && !approved.RequiresStatusCheck, "Viva approval requires completed session");
var sale = handler.Requests.Single(x => x.Url.Contains("transactions:sale")).Json!;
Check(sale["aadeProviderId"]!.GetValue<string>() == "019", "Viva receives three digit ProviderId 019");
Check(sale["aadeProviderSignature"]!.GetValue<string>() == base64, "Viva receives original Base64 DER signature");
Check(sale["aadeProviderSignatureData"]!.GetValue<string>() == "uid-test;;20261101120000;1240;1000;240;1240;VIVA-TID", "Viva signature field order");
Check(sale["amount"]!.GetValue<long>() == 1240 && sale["tipAmount"]!.GetValue<int>() == 0, "Sale amount and tip in cents");
Check(sale["cashRegisterId"]!.GetValue<string>() == "ECR-1" && sale["terminalId"]!.GetValue<string>() == "VIVA-TID", "ECR ID stays distinct from terminal ID");
Check(sale["currencyCode"]!.GetValue<string>() == "978", "Currency wire type retained");
Check(handler.Requests.Count(x => x.Url.Contains("transactions:sale")) == 1 && handler.Requests.Any(x => x.Url.Contains("sessions/")), "One sale followed by session polling");
Check(factory.Names.All(x => x == "Viva-Cloud-Terminal"), "Dedicated Viva HTTP client");

// Validation failures must not start any HTTP traffic.
foreach (var invalid in new[] { "disabled", "tid", "ecr", "credentials", "signature", "amount" }) {
    handler.Requests.Clear(); var settings = Pos(); var provider = data; var amount = 12.40m;
    if (invalid == "disabled") settings.IsEnabled = false;
    if (invalid == "tid") settings.TerminalId = "";
    if (invalid == "ecr") settings.ProviderCode = "";
    if (invalid == "credentials") settings.ApiSecret = "";
    if (invalid == "signature") provider = data with { OriginalSignature = "invalid" };
    if (invalid == "amount") amount = 12.401m;
    var result = await viva.ChargeAsync(settings, amount, "invalid-ref", provider);
    Check(!result.Success && !handler.Requests.Any(x => x.Url.Contains("transactions:sale")), "Invalid " + invalid + " cannot start a sale");
}
handler.Requests.Clear(); handler.Offline = true;
var offline = await viva.ChargeAsync(Pos(), 12.40m, "offline", data);
Check(!offline.Success && !handler.Requests.Any(x => x.Url.Contains("transactions:sale")), "Offline terminal cannot be charged");
handler.Offline = false;
handler.Requests.Clear(); handler.SaleStatus = HttpStatusCode.BadRequest;
var bad = await viva.ChargeAsync(Pos(), 12.40m, "bad-request", data);
Check(!bad.Success && !bad.RequiresStatusCheck && !handler.Requests.Any(x => x.Url.Contains("sessions/")), "Rejected sale is not polled or automatically retried");
handler.SaleStatus = null;
handler.Requests.Clear(); handler.SessionMode = "declined";
var declined = await viva.ChargeAsync(Pos(), 12.40m, "declined", data);
Check(!declined.Success && !declined.RequiresStatusCheck, "Definitive decline is not approval");
handler.Requests.Clear(); handler.SessionMode = "error";
var unknown = await viva.ChargeAsync(Pos(), 12.40m, "unknown", data);
Check(!unknown.Success && unknown.RequiresStatusCheck && handler.Requests.Count(x => x.Url.Contains("transactions:sale")) == 1, "Uncertain session requires status check and is never resubmitted");
handler.SessionMode = "approved";

// Custom URLs including /ecr/v1 remain configurable, and production defaults work.
handler.Requests.Clear(); var custom = Pos(); custom.Endpoint = "https://custom-viva.invalid/ecr/v1/"; custom.AuthEndpoint = "https://custom-auth.invalid/token";
await viva.GetLiveTerminalsAsync(custom);
Check(handler.Requests.First().Url == custom.AuthEndpoint && handler.Requests.Last().Url == "https://custom-viva.invalid/ecr/v1/devices:search", "Custom full ECR base normalized once");
handler.Requests.Clear(); var production = Pos(); production.UseProduction = true;
await viva.GetLiveTerminalsAsync(production);
Check(handler.Requests.First().Url == "https://accounts.vivapayments.com/connect/token" && handler.Requests.Last().Url.StartsWith("https://api.vivapayments.com/"), "Production URL defaults");
// The user's connector accepts a HEX original and normalizes it to Base64 for Viva.
var converter = typeof(VivaCloudTerminalService).GetMethod("TryGetVivaBase64Signature", BindingFlags.NonPublic | BindingFlags.Static)!;
object?[] conversion = { hex, null };
Check((bool)converter.Invoke(null, conversion)! && (string)conversion[1]! == base64, "HEX and Base64 original signatures preserve the same DER bytes");

using var connection = new SqliteConnection("Data Source=:memory:"); connection.Open();
var dbFactory = new FakeDbFactory(new DbContextOptionsBuilder<AppDbContext>().UseSqlite(connection).Options);
using (var db = dbFactory.CreateDbContext()) {
    db.Database.EnsureCreated();
    db.Add(new CompanySettings { VatNumber = "000000000", ParochosSubscriptionKey = "fake-subscription", ParochosUsername = "fake-user", ParochosPassword = "fake-password", ReceiptInvoiceType = "11.1" });
    db.SaveChanges();
}
var fiscal = new FiscalDocumentService(new ParochosService(factory, NullLogger<ParochosService>.Instance), NullLogger<FiscalDocumentService>.Instance);

// Exercise the actual Parochos preparation, Viva sale and fiscal submission for
// both document types and both full/split payment modes.
foreach (bool split in new[] { false, true }) foreach (var kind in new[] { FiscalDocumentKind.Receipt, FiscalDocumentKind.Invoice }) {
    handler.Requests.Clear();
    using var db = dbFactory.CreateDbContext();
    var menu = new MenuItem { Name = "Test item", Price = 6.20m, MenuCategory = new MenuCategory { Name = "Test", VatPercent = 24m } };
    var order = new Order { RestaurantTable = new RestaurantTable { Name = "TEST-" + Guid.NewGuid().ToString("N") }, WaiterUser = new User { FirstName = "Test" },
        CounterpartVatNumber = "000000000", CounterpartName = "Test customer", PaymentMethod = PaymentMethod.Card,
        Items = new() { new OrderItem { MenuItem = menu, Quantity = 1 }, new OrderItem { MenuItem = menu, Quantity = 1 } } };
    db.Add(order); await db.SaveChangesAsync();
    db.Add(new FiscalOrderDocument { OrderId = order.Id, Mark = 10000 + order.Id, ProcessId = "form-process", ExternalSystemId = "form-" + order.Id, SeriesName = "FORM", Number = order.Id });
    await db.SaveChangesAsync();
    var items = split ? order.Items.Take(1).ToList() : order.Items;
    var preparation = split
        ? await fiscal.PrepareSelectedCardPaymentAsync(db, order, items, kind, pos)
        : await fiscal.PrepareCardPaymentAsync(db, order, kind, pos);
    Check(preparation.Success && preparation.ProviderData is not null, $"Prepare {split}/{kind}: {preparation.ErrorMessage}");
    Check(preparation.Signature == base64 && preparation.ProviderData!.OriginalSignature == base64 && preparation.ProviderData.Signature == hex, "Viva preserves original signature for fiscal submission");
    Check(preparation.Amount == (split ? 6.20m : 12.40m) && preparation.NspCode == "6", "Correct selected amount and per-POS NSP");
    var requestPayment = handler.Requests.Last(x => x.Url.EndsWith("/api/requestPayment")).Json!;
    Check(requestPayment["nspCode"]!.GetValue<string>() == "6" && requestPayment["terminalId"]!.GetValue<string>() == "VIVA-TID", "Parochos request uses Viva's selected NSP and TID");
    var charged = await viva.ChargeAsync(pos, preparation.Amount, "flow-test", preparation.ProviderData!);
    Check(charged.Success, "Prepared Viva payment approved by fake API");
    var submittedSale = handler.Requests.Last(x => x.Url.Contains("transactions:sale")).Json!;
    Check(submittedSale["aadeProviderSignatureData"]!.GetValue<string>() == handler.LastSignedText, "Viva receives the exact canonical signed field values");
    FiscalIssueResult issued;
    if (split) {
        issued = await fiscal.IssueSelectedItemsPaymentAsync(db, order, items, PaymentMethod.Card, kind, charged.TransactionId, preparation.Signature,
            preparation.TerminalId, preparation.NspCode, preparation.ExternalSystemId, preparation.SeriesName, preparation.InvoiceNumber);
    } else {
        order.CardTerminalId = preparation.TerminalId; order.CardNspCode = preparation.NspCode; order.CardTransactionId = charged.TransactionId;
        order.ParochosPaymentSignature = preparation.Signature; order.ParochosPaymentUid = preparation.Uid;
        order.ParochosExternalId = preparation.ExternalSystemId; order.InvoiceSeriesName = preparation.SeriesName; order.InvoiceNumber = preparation.InvoiceNumber;
        await db.SaveChangesAsync(); issued = await fiscal.IssueAsync(db, order, kind, charged.TransactionId);
    }
    Check(issued.Success, $"Fiscal submission {split}/{kind}: {issued.ErrorMessage}");
    var send = handler.Requests.Last(x => x.Url.EndsWith("/api/send")).Json!;
    var invoice = send["source"]!["invoice"]!; var method = invoice["paymentMethods"]![0]!;
    Check(method["signature"]!.GetValue<string>() == base64 && method["transactionId"]!.GetValue<string>() == charged.TransactionId, "Fiscal invoice uses approved Viva transaction and original signature");
    Check(method["terminalId"]!.GetValue<string>() == "VIVA-TID" && method["amount"]!.GetValue<decimal>() == preparation.Amount, "Fiscal invoice terminal and amount match approved payment");
    Check(send["externalSystemId"]!.GetValue<string>() == preparation.ExternalSystemId && invoice["invoiceHeader"]!["aa"]!.GetValue<long>() == preparation.InvoiceNumber, "Fiscal submission keeps signed document identity");
}

// Admin settings persistence, environment defaults and checkout eligibility.
var admin = new BankPos(); SetProperty(admin, "DbFactory", dbFactory); Invoke(admin, "NewTerminal");
var edit = GetField<BankPosTerminalSettings>(admin, "edit");
Check(edit.Endpoint == "https://demo-api.vivapayments.com/", "New Viva terminal has Viva demo defaults");
edit.UseProduction = true; Invoke(admin, "EnvironmentChanged");
Check(edit.Endpoint == "https://api.vivapayments.com/" && edit.AuthEndpoint == "https://accounts.vivapayments.com/connect/token", "Admin switches Viva defaults to production");
edit.Endpoint = "https://custom-viva.invalid/"; edit.AuthEndpoint = "https://custom-auth.invalid/token";
edit.UseProduction = false; Invoke(admin, "EnvironmentChanged");
Check(edit.Endpoint == "https://custom-viva.invalid/" && edit.AuthEndpoint == "https://custom-auth.invalid/token", "Custom Viva endpoints preserved");
edit.Name = "Saved Viva"; edit.TerminalId = "VIVA-TID"; edit.ProviderCode = "ECR-1"; edit.ParochosNspCode = "6";
edit.ApiKey = "fake-client"; edit.ApiSecret = "fake-secret"; edit.IsEnabled = true;
await InvokeAsync(admin, "Save"); Check(!GetField<bool>(admin, "isError"), "Viva settings save");
int vivaId = edit.Id; await InvokeAsync(admin, "LoadTerminal", vivaId);
Check(GetField<BankPosTerminalSettings>(admin, "edit").ProviderCode == "ECR-1", "Saved ECR ID reloads");
using (var db = dbFactory.CreateDbContext()) {
    var disabled = Pos(); disabled.IsEnabled = false;
    var unconfigured = Pos(); unconfigured.ApiSecret = "";
    var unsupported = Pos(); unsupported.Protocol = "Viva App-to-App";
    db.AddRange(disabled, unconfigured, unsupported); db.SaveChanges();
}
var checkout = new TableOrder(); SetProperty(checkout, "DbFactory", dbFactory); await InvokeAsync(checkout, "LoadCardPosSettingsAsync");
Check(GetField<List<BankPosTerminalSettings>>(checkout, "cardPosSettings").Select(x => x.Id).SequenceEqual(new[] { vivaId }), "Checkout includes configured enabled Viva Cloud only");
Console.WriteLine($"PASS: {assertions} Viva integration assertions. All HTTP intercepted; no real POS or provider API contacted.");

static void SetProperty(object obj, string name, object value) => obj.GetType().GetProperty(name, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)!.SetValue(obj, value);
static T GetField<T>(object obj, string name) => (T)obj.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(obj)!;
static object? Invoke(object obj, string name, params object[] args) => obj.GetType().GetMethod(name, BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(obj, args);
static async Task InvokeAsync(object obj, string name, params object[] args) => await (Task)Invoke(obj, name, args)!;
sealed class FakeDbFactory(DbContextOptions<AppDbContext> options) : IDbContextFactory<AppDbContext> {
    public AppDbContext CreateDbContext() => new(options);
    public Task<AppDbContext> CreateDbContextAsync(CancellationToken cancellationToken = default) => Task.FromResult(CreateDbContext());
}
sealed class FakeHttpFactory(FakeApi handler) : IHttpClientFactory {
    public List<string> Names { get; } = new();
    public HttpClient CreateClient(string name) { Names.Add(name); return new(handler, false); }
}
sealed record SeenRequest(string Url, string Body, string? AuthScheme) { public JsonNode? Json => string.IsNullOrEmpty(Body) || !Body.TrimStart().StartsWith('{') ? null : JsonNode.Parse(Body); }
sealed class FakeApi(string signature) : HttpMessageHandler {
    public List<SeenRequest> Requests { get; } = new(); public bool Offline { get; set; }
    public HttpStatusCode? SaleStatus { get; set; } public string SessionMode { get; set; } = "approved";
    public string LastSignedText { get; private set; } = ""; private long mark = 20000;
    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) {
        var body = request.Content is null ? "" : await request.Content.ReadAsStringAsync(ct);
        var seen = new SeenRequest(request.RequestUri!.ToString(), body, request.Headers.Authorization?.Scheme); Requests.Add(seen);
        string path = request.RequestUri.AbsolutePath;
        if (path.Contains("loginToSubscription")) return Json(new { jwt = "fake-jwt", jwtExpiration = DateTime.UtcNow.AddHours(1), jwtRefreshToken = "fake-refresh", jwtRefreshTokenExpiration = DateTime.UtcNow.AddHours(2), url1 = "https://fake-parochos.invalid" });
        if (path.EndsWith("/api/requestPayment")) {
            var j = seen.Json!; decimal net = j["netValue"]!.GetValue<decimal>(), vat = j["vatAmount"]!.GetValue<decimal>(), total = j["totalValue"]!.GetValue<decimal>();
            string timestamp = "20261101120000"; string uid = "uid-" + Guid.NewGuid().ToString("N");
            LastSignedText = $"{uid};;{timestamp};{(long)(total*100)};{(long)(net*100)};{(long)(vat*100)};{(long)(total*100)};{j["terminalId"]!.GetValue<string>()}";
            return Json(new { status = 1, externalSystemId = j["externalSystemId"]!.GetValue<string>(), uid,
                paymentToken = new { timestamp = "2026-11-01T12:00:00+02:00", signature, amount = total, signatureClearText = LastSignedText } });
        }
        if (path.EndsWith("/api/send")) return Json(new { status = 1, processId = "test-process", externalSystemId = seen.Json!["externalSystemId"]!.GetValue<string>(), signing = new { mark = ++mark, uid = "issued-uid", authenticationCode = "fake-auth" } });
        if (path.StartsWith("/api/")) return Json(new { status = 1, success = true, connectedCount = 1, pointOfService = "TEST", details = new object[0], balance = 0 });
        if (path.EndsWith("/connect/token") || path.EndsWith("/token")) return Json(new { access_token = "fake-access", expires_in = 3600 });
        if (path.EndsWith("devices:search")) return Json(new[] { new { terminalId = "VIVA-TID", statusId = Offline ? 0 : 1, sourceCode = "test-source", virtualTerminalId = "test-virtual" } });
        if (path.EndsWith("transactions:sale")) return SaleStatus is { } status ? new(status) { Content = new StringContent("{}") } : Json(new { sessionId = seen.Json!["sessionId"]!.GetValue<string>() });
        if (path.Contains("/sessions/")) {
            if (SessionMode == "error") return new(HttpStatusCode.InternalServerError) { Content = new StringContent("{}") };
            return Json(new { success = SessionMode == "approved", eventId = SessionMode == "approved" ? 0 : 1005, transactionId = SessionMode == "approved" ? "viva-txn" : null, authorizationId = "fake-authorization" });
        }
        throw new Exception("Unexpected fake API route");
    }
    static HttpResponseMessage Json(object value) => new(HttpStatusCode.OK) { Content = new StringContent(JsonSerializer.Serialize(value), Encoding.UTF8, "application/json") };
}
