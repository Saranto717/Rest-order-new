using System.Net;
using System.Reflection;
using System.Text;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using RestaurantOrders.Data;
using RestaurantOrders.Models;
using RestaurantOrders.Services;
using RestaurantOrders.Components.Pages.Admin;
using RestaurantOrders.Components.Pages.Waiter;

var assertions = 0;
void Check(bool condition, string message)
{
    if (!condition) throw new Exception(message);
    assertions++;
}
BankPosTerminalSettings Pos(string bank, bool production = true) => new()
{
    Name = bank + " POS", Provider = bank, Protocol = CommonWebEcrProviderProfile.ProtocolName,
    IsEnabled = true, UseProduction = production, TerminalId = "TID-1", IdUrl = "backend-1",
    Endpoint = "https://bank.example.invalid/custom-api/", AuthEndpoint = "https://auth.example.invalid/custom-token/",
    RedeemEndpoint = "https://redeem.example.invalid/custom-redeem/", Username = "test-user", Password = "test-password",
    ApiKey = "test-key", ParochosNspCode = "211"
};
var banks = new[] { "NBG", "Worldline / Cardlink", "Wordline / Cardlink", "myPOS", "GBL", "Pancreta", "Mellon", "NEXI" };
foreach (var bank in banks)
{
    var pos = Pos(bank);
    Check(MellonConnectionSettings.TryResolve(pos, out var prod, out _), bank + " production resolves");
    Check(prod.Endpoint == pos.Endpoint && prod.AuthEndpoint == pos.AuthEndpoint && prod.RedeemEndpoint == pos.RedeemEndpoint, "Custom production endpoints preserved");
    pos.UseProduction = false;
    Check(MellonConnectionSettings.TryResolve(pos, out var demo, out _), bank + " demo resolves");
    Check(demo.Endpoint == MellonConnectionSettings.DemoBaseUrl && demo.AuthEndpoint == MellonConnectionSettings.DemoAuthUrl && demo.RedeemEndpoint == MellonConnectionSettings.DemoRedeemUrl, "All banks use the common demo URLs");
    Check(pos.Endpoint.Contains("bank.example.invalid"), "Demo resolution does not overwrite saved production URLs");
    pos.UseProduction = true;
    Check(MellonConnectionSettings.TryResolve(pos, out var back, out _) && back.Endpoint == prod.Endpoint, "Production round trip");
}
var invalid = Pos("NBG"); invalid.AuthEndpoint = "";
Check(!MellonConnectionSettings.TryResolve(invalid, out _, out _), "Incomplete production rejected");
invalid.AuthEndpoint = MellonConnectionSettings.DemoAuthUrl;
Check(!MellonConnectionSettings.TryResolve(invalid, out _, out _), "Mixed production/demo rejected");
invalid.UseProduction = false; invalid.Endpoint = "not-a-url";
Check(MellonConnectionSettings.TryResolve(invalid, out _, out _), "Demo ignores stored production URLs");
foreach (var provider in new[] { "ePay / Euronet", "EDPS" })
{
    var unrelated = Pos(provider);
    Check(!MellonConnectionSettings.TryResolve(unrelated, out _, out _), "Mellon rejects " + provider);
}

using var connection = new SqliteConnection("Data Source=:memory:");
connection.Open();
var factory = new TestDbFactory(new DbContextOptionsBuilder<AppDbContext>().UseSqlite(connection).Options);
using (var db = factory.CreateDbContext()) db.Database.EnsureCreated();
var handler = new FakeHandler();
var clientFactory = new TestHttpFactory(handler);
var service = new MellonWebEcrService(clientFactory, factory);
var selected = Pos("NBG");
var data = new ProviderPaymentData("uid", "20261101120000", 1000, 250, 1250, 19, "30AB");
var payment = await service.ChargeAsync(selected, 12.50m, "order-ref", providerData: data);
Check(payment.Success && payment.TransactionId == "approved-txn", "Production payment approved");
Check(handler.Requests.Select(x => x.Url).SequenceEqual(new[] {
    selected.AuthEndpoint, selected.Endpoint + "terminal/", selected.Endpoint + "terminal/backend-1/txninit/"
}), "Payment uses only selected POS's custom URLs");
var txn = JsonDocument.Parse(handler.Requests.Last().Body);
Check(txn.RootElement.GetProperty("Amount").GetInt32() == 1250, "Correct amount in cents");
Check(txn.RootElement.GetProperty("ProviderData").GetProperty("ProviderId").GetString() == "19", "Provider signature payload retained");
Check(handler.Requests.Skip(1).All(x => x.ApiKey == selected.ApiKey), "Selected POS API key retained");
Check(clientFactory.Names.All(x => x == "Mellon-WebECR"), "Dedicated Mellon HTTP client");
handler.Requests.Clear(); selected.UseProduction = false;
payment = await service.ChargeAsync(selected, 12.50m, "demo-ref", providerData: data);
Check(payment.Success, "Demo payment approved by fake server");
Check(handler.Requests.All(x => new Uri(x.Url).Host == "uat.mreceipts.com"), "Demo never contacts production, including auth");
handler.Requests.Clear(); handler.ReturnWrongTid = true;
payment = await service.ChargeAsync(selected, 12.50m, "wrong-tid", providerData: data);
Check(!payment.Success && !handler.Requests.Any(x => x.Url.Contains("txninit")), "Wrong physical TID cannot be charged");
handler.ReturnWrongTid = false; handler.Requests.Clear(); selected.IdUrl = "wrong-backend";
payment = await service.ChargeAsync(selected, 12.50m, "wrong-backend", providerData: data);
Check(!payment.Success && !handler.Requests.Any(x => x.Url.Contains("txninit")), "Wrong backend cannot be charged");
handler.Requests.Clear(); selected.IdUrl = "";
payment = await service.ChargeAsync(selected, 12.50m, "resolve-tid", providerData: data);
Check(payment.Success, "Backend may be resolved by the configured physical TID");
handler.Requests.Clear(); selected.IsEnabled = false;
payment = await service.ChargeAsync(selected, 12.50m, "disabled", providerData: data);
Check(!payment.Success && handler.Requests.Count == 0, "Disabled POS cannot contact payment API");

// Exercise setup persistence and the real private UI selection code with an isolated in-memory database.
var stored = Pos("Pancreta"); stored.ApiKey = ""; stored.RedeemCode = "  one-time  ";
using (var db = factory.CreateDbContext()) { db.Add(stored); db.SaveChanges(); }
handler.Requests.Clear();
var redeem = await service.RedeemAsync(stored);
Check(redeem.Success && handler.Requests.Last().Url == stored.RedeemEndpoint, "Redeem uses custom production endpoint");
using (var redeemJson = JsonDocument.Parse(handler.Requests.Last().Body))
{
    var payload = redeemJson.RootElement;
    Check(payload.GetProperty("Type").GetString() == "webecr", "Setup Redeem sends case-sensitive Type");
    Check(payload.GetProperty("Code").GetString() == "one-time", "Setup Redeem sends case-sensitive trimmed Code");
    Check(payload.EnumerateObject().Count() == 2, "Redeem has exactly the two required fields");
}
using (var tokenJson = JsonDocument.Parse(handler.Requests.First().Body))
{
    Check(tokenJson.RootElement.GetProperty("username").GetString() == stored.Username &&
        tokenJson.RootElement.GetProperty("password").GetString() == stored.Password,
        "Authentication keeps its lowercase username/password fields");
}
using (var db = factory.CreateDbContext())
{
    var saved = db.BankPosTerminalSettings.Single(x => x.Id == stored.Id);
    Check(saved.ApiKey == "redeemed-key" && saved.RedeemCode == "", "Redeem persists only the selected POS pairing");
    Check(saved.Endpoint == stored.Endpoint, "Redeem preserves production settings");
}
var admin = new BankPos();
SetProperty(admin, "DbFactory", factory);
await InvokeAsync(admin, "LoadTerminal", stored.Id);
var edit = GetField<BankPosTerminalSettings>(admin, "edit");
edit.UseProduction = false;
Invoke(admin, "EnvironmentChanged");
Check(edit.Endpoint == stored.Endpoint && edit.AuthEndpoint == stored.AuthEndpoint && edit.RedeemEndpoint == stored.RedeemEndpoint, "Admin environment switch preserves all production URLs");
Check(edit.ApiKey == "" && edit.IdUrl == "", "Environment switch requires pairing in the new environment");
await InvokeAsync(admin, "Save");
await InvokeAsync(admin, "LoadTerminal", stored.Id);
edit = GetField<BankPosTerminalSettings>(admin, "edit");
Check(edit.Endpoint == stored.Endpoint && !edit.UseProduction, "Save/reload preserves URLs and chosen environment");

var active = Pos("GBL");
var disabled = Pos("NBG"); disabled.IsEnabled = false;
var unpaired = Pos("myPOS"); unpaired.ApiKey = "";
var missingTid = Pos("Pancreta"); missingTid.TerminalId = "";
var badProduction = Pos("Mellon"); badProduction.Endpoint = "";
var epay = new BankPosTerminalSettings { Provider = "ePay / Euronet", Protocol = "ePay WebECR REST API v2.5.13", IsEnabled = true };
var edps = new BankPosTerminalSettings { Provider = "EDPS", Protocol = "EDPS Middleware (Chronos WebECR API v6)", IsEnabled = true };
using (var db = factory.CreateDbContext()) { db.AddRange(active, disabled, unpaired, missingTid, badProduction, epay, edps); db.SaveChanges(); }
var checkout = new TableOrder();
SetProperty(checkout, "DbFactory", factory);
await InvokeAsync(checkout, "LoadCardPosSettingsAsync");
var choices = GetField<List<BankPosTerminalSettings>>(checkout, "cardPosSettings");
Check(choices.Select(x => x.Id).Order().SequenceEqual(new[] { active.Id, epay.Id, edps.Id }.Order()), "Checkout lists connected active Mellon POS and retains existing ePay/EDPS choices");
// The same Redeem contract applies when a charge first redeems a one-time code.
var automaticRedeem = Pos("NBG", production: false);
automaticRedeem.ApiKey = ""; automaticRedeem.RedeemCode = "  automatic-code  ";
using (var db = factory.CreateDbContext()) { db.Add(automaticRedeem); db.SaveChanges(); }
handler.Requests.Clear();
var automaticPayment = await service.ChargeAsync(automaticRedeem, 12.50m, "automatic-redeem", providerData: data);
Check(automaticPayment.Success, "Charge can redeem a code with exact Type/Code names");
var automaticRequest = handler.Requests.Single(x => x.Url == MellonConnectionSettings.DemoRedeemUrl);
using (var automaticJson = JsonDocument.Parse(automaticRequest.Body))
{
    Check(automaticJson.RootElement.GetProperty("Type").GetString() == "webecr" &&
        automaticJson.RootElement.GetProperty("Code").GetString() == "automatic-code",
        "Automatic Redeem uses the same exact field names and trimmed code");
}
Check(handler.Requests.All(x => new Uri(x.Url).Host == "uat.mreceipts.com"), "Automatic demo Redeem stays in demo");
Check(handler.Requests.Last().ApiKey == "redeemed-key", "Charge uses the API key returned by Redeem");
using (var db = factory.CreateDbContext())
{
    var saved = db.BankPosTerminalSettings.Single(x => x.Id == automaticRedeem.Id);
    Check(saved.ApiKey == "redeemed-key" && saved.RedeemCode == "", "Automatic Redeem persists the key and clears the code");
}

// Mellon must never inherit ePay's fallback NSP code, for either payment mode.
var fiscal = new FiscalDocumentService(null!, Microsoft.Extensions.Logging.Abstractions.NullLogger<FiscalDocumentService>.Instance);
var noNsp = Pos("NBG"); noNsp.ParochosNspCode = "";
using (var db = factory.CreateDbContext())
{
    var fullPreparation = await fiscal.PrepareCardPaymentAsync(db, new Order(), FiscalDocumentKind.Receipt, noNsp);
    var splitPreparation = await fiscal.PrepareSelectedCardPaymentAsync(db, new Order(), Array.Empty<OrderItem>(), FiscalDocumentKind.Receipt, noNsp);
    Check(!fullPreparation.Success && fullPreparation.ErrorMessage!.Contains("NSP"), "Full Mellon payment requires its own NSP");
    Check(!splitPreparation.Success && splitPreparation.ErrorMessage!.Contains("NSP"), "Split Mellon payment requires its own NSP");
}
Console.WriteLine($"PASS: {assertions} regression assertions. HTTP requests were intercepted; no real POS was contacted.");

static void SetProperty(object obj, string name, object value) => obj.GetType().GetProperty(name, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)!.SetValue(obj, value);
static T GetField<T>(object obj, string name) => (T)obj.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(obj)!;
static object? Invoke(object obj, string name, params object[] args) => obj.GetType().GetMethod(name, BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(obj, args);
static async Task InvokeAsync(object obj, string name, params object[] args) => await (Task)Invoke(obj, name, args)!;
sealed class TestDbFactory(DbContextOptions<AppDbContext> options) : IDbContextFactory<AppDbContext>
{
    public AppDbContext CreateDbContext() => new(options);
    public Task<AppDbContext> CreateDbContextAsync(CancellationToken cancellationToken = default) => Task.FromResult(CreateDbContext());
}
sealed class TestHttpFactory(FakeHandler handler) : IHttpClientFactory
{
    public List<string> Names { get; } = new();
    public HttpClient CreateClient(string name) { Names.Add(name); return new HttpClient(handler, false); }
}
sealed class FakeHandler : HttpMessageHandler
{
    public List<(string Url, string Body, string ApiKey)> Requests { get; } = new();
    public bool ReturnWrongTid { get; set; }
    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var body = request.Content is null ? "" : await request.Content.ReadAsStringAsync(cancellationToken);
        Requests.Add((request.RequestUri!.ToString(), body, request.Headers.TryGetValues("X-Api-Key", out var keys) ? keys.Single() : ""));
        var path = request.RequestUri.AbsolutePath;
        if (path.Contains("redeem"))
        {
            using var json = JsonDocument.Parse(body);
            var payload = json.RootElement;
            if (!payload.TryGetProperty("Type", out var type) || type.GetString() != "webecr" ||
                !payload.TryGetProperty("Code", out var code) || string.IsNullOrWhiteSpace(code.GetString()))
                return new HttpResponseMessage(HttpStatusCode.BadRequest)
                {
                    Content = new StringContent("""{"Type":["This field is required."],"Code":["This field is required."]}""", Encoding.UTF8, "application/json")
                };
        }
        var response = request.Method == HttpMethod.Get
            ? JsonSerializer.Serialize(new { results = new[] { new { id = "backend-1", TerminalID = ReturnWrongTid ? "another-TID" : "TID-1", Merchant = "merchant", Acquirer = "bank" } } })
            : path.Contains("txninit") ? """{"Id":"intent-1","Status":3,"Result":1,"TransactionId":"approved-txn","Transaction":{"Id":"approved-txn","Approved":true}}"""
            : path.Contains("redeem") ? """{"Id":"redeemed-key"}"""
            : """{"access":"test-access","refresh":"test-refresh"}""";
        return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(response, Encoding.UTF8, "application/json") };
    }
}
