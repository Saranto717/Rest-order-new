using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text;
using System.Globalization;
using RestaurantOrders.Models;
using RestaurantOrders.Data;
using Microsoft.EntityFrameworkCore;

namespace RestaurantOrders.Services;

/// <summary>Client for ePay and banks using the common Mellon-compatible WebECR REST API.</summary>
public sealed class EpayWebEcrService(IHttpClientFactory httpClientFactory, IDbContextFactory<AppDbContext> dbFactory)
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        PropertyNameCaseInsensitive = true
    };

    public sealed record PaymentResult(bool Success, string TransactionId, string Message, string? IntentId = null);
    public sealed record TerminalChoice(string BackendId, string TerminalId, string Merchant, string Acquirer);
    public sealed record SetupResult(bool Success, string Message, string? ApiKey = null, IReadOnlyList<TerminalChoice>? Terminals = null);

    /// <summary>Redeems the one-time code shown by the POS and persists the resulting API key.</summary>
    public async Task<SetupResult> RedeemAsync(BankPosTerminalSettings settings, CancellationToken cancellationToken = default)
    {
        if (!CommonWebEcrProviderProfile.SupportsPayment(settings)) return new(false, "Ο επιλεγμένος πάροχος/τρόπος σύνδεσης δεν χρησιμοποιεί το κοινό WebECR REST API.");
        if (settings.Id <= 0) return new(false, "Αποθήκευσε πρώτα τη ρύθμιση του τερματικού.");
        if (string.IsNullOrWhiteSpace(settings.Endpoint) || string.IsNullOrWhiteSpace(settings.Username) || string.IsNullOrWhiteSpace(settings.Password))
            return new(false, "Συμπλήρωσε Base URL, Username και Password.");
        if (string.IsNullOrWhiteSpace(settings.RedeemCode)) return new(false, "Βάλε τον one-time κωδικό που εμφανίζει το POS.");
        if (!TryBaseUri(settings.Endpoint, out var baseUri, out var error)) return new(false, error);
        var client = httpClientFactory.CreateClient("Common-WebECR"); client.BaseAddress = baseUri; client.Timeout = TimeSpan.FromSeconds(30);
        client.DefaultRequestHeaders.Accept.Clear();
        client.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        try
        {
            using var tokenResponse = await PostJsonExactAsync(client, ResolveEndpoint(settings.Endpoint, settings.AuthEndpoint, "token/"), new { username = settings.Username.Trim(), password = settings.Password }, cancellationToken);
            var tokenBody = await tokenResponse.Content.ReadAsStringAsync(cancellationToken);
            if (!tokenResponse.IsSuccessStatusCode) return new(false, AuthenticationError(ResolveEndpoint(settings.Endpoint, settings.AuthEndpoint, "token/"), tokenResponse.StatusCode, tokenBody, settings.Provider) + " Έλεγξε ότι τα Username/Password είναι WebECR API credentials για το ίδιο περιβάλλον.");
            var token = JsonSerializer.Deserialize<TokenResponse>(tokenBody, JsonOptions);
            if (string.IsNullOrWhiteSpace(token?.access)) return new(false, $"Το {CommonWebEcrProviderProfile.ProviderLabel(settings.Provider)} WebECR δεν επέστρεψε access token.");
            client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token.access);
            using var response = await PostJsonExactAsync(client, ResolveEndpoint(settings.Endpoint, settings.RedeemEndpoint, "authorization/redeem/"), new { Type = "webecr", Code = settings.RedeemCode.Trim() }, cancellationToken);
            var body = await response.Content.ReadAsStringAsync(cancellationToken);
            if (!response.IsSuccessStatusCode) return new(false, ApiError("Redeem API Key", response.StatusCode, body));
            var redeemed = JsonSerializer.Deserialize<RedeemResponse>(body, JsonOptions);
            if (string.IsNullOrWhiteSpace(redeemed?.Id)) return new(false, "Η απάντηση Redeem δεν περιείχε API key (Id).");
            using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
            var saved = await db.BankPosTerminalSettings.FirstOrDefaultAsync(x => x.Id == settings.Id, cancellationToken);
            if (saved is null) return new(false, "Το τερματικό δεν βρέθηκε στη βάση για αποθήκευση του API key.");
            saved.ApiKey = redeemed.Id; saved.RedeemCode = string.Empty; saved.UpdatedAt = DateTime.UtcNow;
            await db.SaveChangesAsync(cancellationToken);
            settings.ApiKey = redeemed.Id; settings.RedeemCode = string.Empty;
            return new(true, "Το Redeem ολοκληρώθηκε. Το API Key αποθηκεύτηκε.", redeemed.Id);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested) { return new(false, "Έληξε ο χρόνος αναμονής κατά το Redeem."); }
        catch (HttpRequestException ex) { return new(false, "Αδυναμία επικοινωνίας με WebECR: " + ex.Message); }
        catch (JsonException) { return new(false, "Το WebECR επέστρεψε απάντηση που δεν αναγνωρίστηκε."); }
    }

    /// <summary>
    /// Returns a copy of the terminal settings pointing at another Mellon bank (same credentials),
    /// or the same instance when no override applies. The copy has Id = 0 so it is never persisted.
    /// </summary>
    public static BankPosTerminalSettings ApplyBankOverride(BankPosTerminalSettings settings, string? bankProviderOverride)
    {
        if (string.IsNullOrWhiteSpace(bankProviderOverride) ||
            CommonWebEcrProviderProfile.BaseUrlFor(bankProviderOverride, settings.UseProduction) is not { } overrideBase ||
            string.Equals(CommonWebEcrProviderProfile.BankGroupFor(bankProviderOverride), CommonWebEcrProviderProfile.BankGroupFor(settings.Provider), StringComparison.Ordinal))
            return settings;
        return new BankPosTerminalSettings
        {
            Id = 0,
            Name = settings.Name, Provider = bankProviderOverride.Trim(), Protocol = CommonWebEcrProviderProfile.ProtocolName,
            UseProduction = settings.UseProduction, IsEnabled = settings.IsEnabled, TerminalId = settings.TerminalId,
            IdUrl = "", RedeemCode = "", ProviderCode = settings.ProviderCode,
            ParochosNspCode = settings.ParochosNspCode, ProviderId = settings.ProviderId,
            ApiKey = settings.ApiKey, Username = settings.Username, Password = settings.Password,
            Endpoint = overrideBase + "/",
            AuthEndpoint = CommonWebEcrProviderProfile.AuthUrlFor(bankProviderOverride, settings.UseProduction) ?? "",
            RedeemEndpoint = CommonWebEcrProviderProfile.RedeemUrlFor(bankProviderOverride, settings.UseProduction) ?? "",
            TimeoutSeconds = settings.TimeoutSeconds
        };
    }

    /// <summary>Retrieves permitted terminals and maps the backend ID separately from the physical TID.</summary>
    public async Task<SetupResult> GetTerminalsAsync(BankPosTerminalSettings settings, CancellationToken cancellationToken = default, string? bankProviderOverride = null)
    {
        settings = ApplyBankOverride(settings, bankProviderOverride);
        if (!CommonWebEcrProviderProfile.SupportsPayment(settings)) return new(false, "Ο επιλεγμένος πάροχος/τρόπος σύνδεσης δεν χρησιμοποιεί το κοινό WebECR REST API.");
        if (string.IsNullOrWhiteSpace(settings.ApiKey)) return new(false, "Κάνε πρώτα Redeem και αποθήκευσε το API Key.");
        if (string.IsNullOrWhiteSpace(settings.Endpoint) || string.IsNullOrWhiteSpace(settings.Username) || string.IsNullOrWhiteSpace(settings.Password)) return new(false, "Συμπλήρωσε Base URL, Username και Password.");
        if (!TryBaseUri(settings.Endpoint, out var baseUri, out var error)) return new(false, error);
        var client = httpClientFactory.CreateClient("Common-WebECR"); client.BaseAddress = baseUri; client.Timeout = TimeSpan.FromSeconds(30);
        client.DefaultRequestHeaders.Accept.Clear();
        client.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        try
        {
            using var tokenResponse = await PostJsonExactAsync(client, ResolveEndpoint(settings.Endpoint, settings.AuthEndpoint, "token/"), new { username = settings.Username.Trim(), password = settings.Password }, cancellationToken);
            var tokenBody = await tokenResponse.Content.ReadAsStringAsync(cancellationToken);
            if (!tokenResponse.IsSuccessStatusCode) return new(false, AuthenticationError(ResolveEndpoint(settings.Endpoint, settings.AuthEndpoint, "token/"), tokenResponse.StatusCode, tokenBody, settings.Provider) + " Έλεγξε ότι τα Username/Password είναι WebECR API credentials για το ίδιο περιβάλλον.");
            var token = JsonSerializer.Deserialize<TokenResponse>(tokenBody, JsonOptions);
            if (string.IsNullOrWhiteSpace(token?.access)) return new(false, $"Το {CommonWebEcrProviderProfile.ProviderLabel(settings.Provider)} WebECR δεν επέστρεψε access token.");
            client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token.access);
            client.DefaultRequestHeaders.TryAddWithoutValidation("X-Api-Key", settings.ApiKey.Trim());
            using var response = await client.GetAsync("terminal/", cancellationToken);
            var body = await response.Content.ReadAsStringAsync(cancellationToken);
            if (!response.IsSuccessStatusCode) return new(false, ApiError("Get active terminals", response.StatusCode, body));
            var list = JsonSerializer.Deserialize<TerminalListResponse>(body, JsonOptions);
            var choices = (list?.results ?? new()).Where(t => !string.IsNullOrWhiteSpace(t.id) && !string.IsNullOrWhiteSpace(t.TerminalID))
                .Select(t => new TerminalChoice(t.id!, t.TerminalID!, t.Merchant ?? "", t.Acquirer ?? "")).ToList();
            if (choices.Count == 0) return new(true, "Η σύνδεση πέτυχε, αλλά δεν επιστράφηκαν διαθέσιμα τερματικά. Έλεγξε τις εξουσιοδοτήσεις WebECR για το POS.", settings.ApiKey, choices);
            return new(true, $"Βρέθηκαν {choices.Count} διαθέσιμα τερματικά.", settings.ApiKey, choices);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested) { return new(false, "Έληξε ο χρόνος αναμονής κατά την αναζήτηση τερματικών."); }
        catch (HttpRequestException ex) { return new(false, "Αδυναμία επικοινωνίας με WebECR: " + ex.Message); }
        catch (JsonException) { return new(false, "Το WebECR επέστρεψε απάντηση που δεν αναγνωρίστηκε."); }
    }


    /// <summary>
    /// Sends JSON using the same explicit Content-Type as the successful Postman request.
    /// In particular, Content-Type is application/json without an added charset parameter.
    /// </summary>
    private static Task<HttpResponseMessage> PostJsonExactAsync<T>(HttpClient client, string url, T payload, CancellationToken cancellationToken)
    {
        var json = JsonSerializer.Serialize(payload, JsonOptions);
        var content = new StringContent(json, Encoding.UTF8);
        content.Headers.ContentType = new MediaTypeHeaderValue("application/json");
        return client.PostAsync(url, content, cancellationToken);
    }

    private static string ResolveEndpoint(string baseUrl, string configuredUrl, string fallbackPath)
    {
        var value = string.IsNullOrWhiteSpace(configuredUrl)
            ? new Uri(new Uri(baseUrl.Trim().TrimEnd('/') + "/"), fallbackPath).ToString()
            : configuredUrl.Trim();
        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri) || (uri.Scheme != Uri.UriSchemeHttps && uri.Scheme != Uri.UriSchemeHttp))
            throw new InvalidOperationException($"Μη έγκυρο endpoint URL: {value}");
        return uri.ToString();
    }

    private static string ResolveRefreshEndpoint(string baseUrl, string configuredAuthUrl)
    {
        if (string.IsNullOrWhiteSpace(configuredAuthUrl))
            return ResolveEndpoint(baseUrl, "", "token/refresh/");

        var authUri = new Uri(configuredAuthUrl.Trim(), UriKind.Absolute);
        var refreshUrl = authUri.AbsoluteUri.TrimEnd('/') + "/refresh/";
        if (!Uri.TryCreate(refreshUrl, UriKind.Absolute, out var refreshUri) ||
            (refreshUri.Scheme != Uri.UriSchemeHttps && refreshUri.Scheme != Uri.UriSchemeHttp))
            throw new InvalidOperationException("Invalid token refresh endpoint URL.");
        return refreshUri.ToString();
    }

    private static bool TryBaseUri(string endpoint, out Uri baseUri, out string error)
    {
        if (Uri.TryCreate(endpoint.Trim().TrimEnd('/') + "/", UriKind.Absolute, out var uri) && (uri.Scheme == Uri.UriSchemeHttps || uri.Scheme == Uri.UriSchemeHttp))
        { baseUri = uri; error = ""; return true; }
        baseUri = new Uri("https://localhost/"); error = "Το Base URL δεν είναι έγκυρο HTTP/HTTPS URL."; return false;
    }
    private sealed record TokenResponse(string? access, string? refresh);
    private sealed record RedeemResponse(string? Id, string? Type);
    private sealed record TerminalListResponse(List<TerminalDto>? results);
    private sealed record TerminalDto(string? id, string? TerminalID, string? Merchant, string? Acquirer);
    private sealed record IntentResponse(string? Id, int Status, int Result, int TxnType, int Amount,
        string? TransactionId, string? ResultCommand, TransactionDto? Transaction);
    private sealed record TransactionDto(string? Id, bool Approved, string? RRN, string? AuthCode);

    public async Task<PaymentResult> ChargeAsync(BankPosTerminalSettings settings, decimal amount, string customerReference, CancellationToken cancellationToken = default, string? selectedBackendId = null, ProviderPaymentData? providerData = null, string? bankProviderOverride = null)
    {
        // Per-payment bank selection: the operator may pick a different Mellon bank than the
        // terminal's default. Only the base/auth/redeem URLs change; credentials stay the same.
        var overridden = ApplyBankOverride(settings, bankProviderOverride);
        if (!ReferenceEquals(overridden, settings))
        {
            settings = overridden;
            selectedBackendId = null; // backend terminal IDs are per bank; resolve again by TID
        }
        if (!settings.IsEnabled) return Fail("Το επιλεγμένο POS δεν είναι ενεργό στις ρυθμίσεις.");
        if (!CommonWebEcrProviderProfile.SupportsPayment(settings))
            return Fail("Το επιλεγμένο τερματικό δεν έχει ρυθμιστεί για το κοινό WebECR REST API.");
        if (string.IsNullOrWhiteSpace(settings.Endpoint)) return Fail("Συμπλήρωσε το Base URL του WebECR στο πεδίο API Endpoint.");
        if (string.IsNullOrWhiteSpace(settings.Username) || string.IsNullOrWhiteSpace(settings.Password)) return Fail("Συμπλήρωσε τα WebECR Username και Password του παρόχου.");
        if (amount <= 0) return Fail("Το ποσό της συναλλαγής πρέπει να είναι μεγαλύτερο από μηδέν.");

        if (!Uri.TryCreate(settings.Endpoint.Trim().TrimEnd('/') + "/", UriKind.Absolute, out var baseUri) || (baseUri.Scheme != Uri.UriSchemeHttps && baseUri.Scheme != Uri.UriSchemeHttp))
            return Fail("Το Base URL δεν είναι έγκυρο HTTP/HTTPS URL.");

        var client = httpClientFactory.CreateClient("Common-WebECR");
        client.BaseAddress = baseUri;
        // The successful ePay sample uses 180 seconds. Older saved POS rows may
        // still carry the application's old 60-second default, so treat that
        // legacy default as the WebECR sample's 180-second timeout.
        var configuredTimeout = settings.TimeoutSeconds == 60 ? 180 : settings.TimeoutSeconds;
        var timeoutSeconds = Math.Clamp(configuredTimeout, 30, 180);
        client.Timeout = TimeSpan.FromSeconds(Math.Min(timeoutSeconds + 10, 190));
        client.DefaultRequestHeaders.Accept.Clear();
        client.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));

        try
        {
            using var tokenResponse = await PostJsonExactAsync(client, ResolveEndpoint(settings.Endpoint, settings.AuthEndpoint, "token/"), new { username = settings.Username.Trim(), password = settings.Password }, cancellationToken);
            var tokenBody = await tokenResponse.Content.ReadAsStringAsync(cancellationToken);
            if (!tokenResponse.IsSuccessStatusCode) return Fail(AuthenticationError(ResolveEndpoint(settings.Endpoint, settings.AuthEndpoint, "token/"), tokenResponse.StatusCode, tokenBody, settings.Provider) + " Έλεγξε ότι τα Username/Password είναι WebECR API credentials για το ίδιο περιβάλλον.");
            var token = JsonSerializer.Deserialize<TokenResponse>(tokenBody, JsonOptions);
            if (string.IsNullOrWhiteSpace(token?.access)) return Fail($"Το {CommonWebEcrProviderProfile.ProviderLabel(settings.Provider)} WebECR δεν επέστρεψε access token.");
            client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token.access);

            // A saved API key means permissions were previously redeemed. Otherwise redeem the code supplied by the POS.
            var apiKey = settings.ApiKey?.Trim();
            if (string.IsNullOrWhiteSpace(apiKey) && !string.IsNullOrWhiteSpace(settings.RedeemCode))
            {
                using var redeemResponse = await PostJsonExactAsync(client, ResolveEndpoint(settings.Endpoint, settings.RedeemEndpoint, "authorization/redeem/"), new { Type = "webecr", Code = settings.RedeemCode.Trim() }, cancellationToken);
                var redeemBody = await redeemResponse.Content.ReadAsStringAsync(cancellationToken);
                if (!redeemResponse.IsSuccessStatusCode) return Fail(ApiError("Redeem authorization code", redeemResponse.StatusCode, redeemBody));
                var redeemed = JsonSerializer.Deserialize<RedeemResponse>(redeemBody, JsonOptions);
                apiKey = redeemed?.Id;
                if (string.IsNullOrWhiteSpace(apiKey)) return Fail($"Το {CommonWebEcrProviderProfile.ProviderLabel(settings.Provider)} δεν επέστρεψε API key από το redeem.");
                // The redeem code is one-time. Persist the resulting API key so subsequent payments do not redeem it again.
                if (settings.Id > 0)
                {
                    using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
                    var saved = await db.BankPosTerminalSettings.FirstOrDefaultAsync(x => x.Id == settings.Id, cancellationToken);
                    if (saved is not null) { saved.ApiKey = apiKey; saved.RedeemCode = string.Empty; saved.UpdatedAt = DateTime.UtcNow; await db.SaveChangesAsync(cancellationToken); }
                }
            }
            if (!string.IsNullOrWhiteSpace(apiKey)) client.DefaultRequestHeaders.TryAddWithoutValidation("X-Api-Key", apiKey);

            string terminalBackendId;
            if (!string.IsNullOrWhiteSpace(selectedBackendId))
            {
                // Backend ID came from the terminal the operator explicitly selected in the UI.
                terminalBackendId = selectedBackendId.Trim();
            }
            else if (!string.IsNullOrWhiteSpace(settings.IdUrl))
            {
                terminalBackendId = settings.IdUrl.Trim();
            }
            else
            {
                using var terminalResponse = await client.GetAsync("terminal/", cancellationToken);
                var terminalBody = await terminalResponse.Content.ReadAsStringAsync(cancellationToken);
                if (!terminalResponse.IsSuccessStatusCode) return Fail(ApiError("List terminals", terminalResponse.StatusCode, terminalBody));
                var terminalList = JsonSerializer.Deserialize<TerminalListResponse>(terminalBody, JsonOptions);
                var match = terminalList?.results?.FirstOrDefault(t => !string.IsNullOrWhiteSpace(settings.TerminalId) && string.Equals(t.TerminalID, settings.TerminalId.Trim(), StringComparison.OrdinalIgnoreCase));
                if (match is null || string.IsNullOrWhiteSpace(match.id))
                    return Fail("Δεν βρέθηκε τερματικό WebECR. Πάτησε «Ανανέωση POS» και επίλεξε διαθέσιμο τερματικό.");
                terminalBackendId = match.id;
            }

            var cents = decimal.ToInt32(decimal.Round(amount * 100m, 0, MidpointRounding.AwayFromZero));
            // Keep the transaction-init body aligned with the documented WebECR schema.
            // Use the same explicit application/json content type that fixed the token/redeem calls;
            // PostAsJsonAsync adds a charset parameter and had not been used by the successful requests.
            var request = new Dictionary<string, object?>
            {
                ["TxnType"] = 0,
                ["Amount"] = cents,
                ["TipAmount"] = 0,
                ["CurrencyCode"] = 978,
                ["CustomerReference"] = string.IsNullOrWhiteSpace(customerReference) ? null : customerReference.Length > 50 ? customerReference[..50] : customerReference,
                ["Timeout"] = timeoutSeconds,
                ["CustomerPhone"] = null,
                ["CustomerEmail"] = null
            };
            if (providerData is not null)
            {
                var providerPayload = new Dictionary<string, object?>
                {
                    // The proven ePay request sends ProviderId as a JSON string.
                    ["ProviderId"] = providerData.ProviderId.ToString(CultureInfo.InvariantCulture),
                    ["Uid"] = providerData.Uid,
                    ["SignatureTimestamp"] = providerData.SignatureTimestamp,
                    ["NetAmount"] = providerData.NetAmount,
                    ["VatAmount"] = providerData.VatAmount,
                    ["TotalAmount"] = providerData.TotalAmount,
                    ["Signature"] = providerData.Signature
                };
                request["ProviderData"] = providerPayload;
            }
            var txnInitUrl = $"terminal/{Uri.EscapeDataString(terminalBackendId)}/txninit/";
            using var intentResponse = await PostJsonExactAsync(client, txnInitUrl, request, cancellationToken);
            var intentBody = await intentResponse.Content.ReadAsStringAsync(cancellationToken);
            var intentStatus = intentResponse.StatusCode;
            if (intentResponse.StatusCode == System.Net.HttpStatusCode.Unauthorized &&
                intentBody.Contains("invalid provider signature", StringComparison.OrdinalIgnoreCase))
            {
                // A Parochos payment signature is single-use. Do not submit the same
                // token a second time with a different encoding; the known successful
                // ePay request format uses DER HEX. A new user attempt will obtain a
                // fresh RequestPayment token before txninit.
                return Fail("ePay rejected the Parochos signature. " +
                    BuildProviderSignatureDiagnostic(providerData, settings.TerminalId, terminalBackendId, cents) +
                    " No charge was started.");
            }
            if (intentStatus == System.Net.HttpStatusCode.Unauthorized && !string.IsNullOrWhiteSpace(token.refresh))
            {
                // A 401 means the transaction-init request was not authorized; refresh and retry once.
                // AuthEndpoint is the login URL (normally /token/). Refresh is its
                // sibling route /token/refresh/ and does not use the expired access token.
                var expiredAuthorization = client.DefaultRequestHeaders.Authorization;
                client.DefaultRequestHeaders.Authorization = null;
                using var refreshResponse = await PostJsonExactAsync(client, ResolveRefreshEndpoint(settings.Endpoint, settings.AuthEndpoint), new { refresh = token.refresh }, cancellationToken);
                var refreshBody = await refreshResponse.Content.ReadAsStringAsync(cancellationToken);
                client.DefaultRequestHeaders.Authorization = expiredAuthorization;
                if (refreshResponse.IsSuccessStatusCode)
                {
                    var refreshed = JsonSerializer.Deserialize<TokenResponse>(refreshBody, JsonOptions);
                    if (!string.IsNullOrWhiteSpace(refreshed?.access))
                    {
                        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", refreshed.access);
                        intentResponse.Dispose();
                        using var retryResponse = await PostJsonExactAsync(client, txnInitUrl, request, cancellationToken);
                        intentBody = await retryResponse.Content.ReadAsStringAsync(cancellationToken);
                        if (!retryResponse.IsSuccessStatusCode) return Fail(ApiError("Start card transaction after token refresh", retryResponse.StatusCode, intentBody));
                    }
                    else return Fail("Το ePay token refresh δεν επέστρεψε access token.");
                }
                else return Fail(ApiError("Refresh access token", refreshResponse.StatusCode, refreshBody));
            }
            else if ((int)intentStatus < 200 || (int)intentStatus > 299) return Fail(ApiError("Start card transaction", intentStatus, intentBody));
            var intent = JsonSerializer.Deserialize<IntentResponse>(intentBody, JsonOptions);
            if (intent is null) return Fail("Το ePay επέστρεψε μη αναγνώσιμη απάντηση συναλλαγής.");
            if (intent.Result != 1 || intent.Status != 3 || intent.Transaction?.Approved != true)
            {
                var resultText = intent.Result switch { 1 => "Εγκρίθηκε αλλά η κατάσταση δεν είναι ολοκληρωμένη", 2 => "Απορρίφθηκε", 3 => "Ακυρώθηκε στο POS", 4 => "Αποτυχία συναλλαγής", 5 => "Άγνωστο αποτέλεσμα — έλεγξε το POS πριν επαναλάβεις", 6 => "Το POS είναι απασχολημένο", 7 => "Έφτασε το όριο συναλλαγών του POS", _ => $"Μη αναγνωρισμένο αποτέλεσμα ({intent.Result})" };
                return Fail($"Η πληρωμή δεν επιβεβαιώθηκε ως εγκεκριμένη: {resultText}. Δεν έγινε έκδοση παραστατικού.", intent.Id);
            }

            var transactionId = !string.IsNullOrWhiteSpace(intent.TransactionId) ? intent.TransactionId : intent.Transaction?.Id;
            if (string.IsNullOrWhiteSpace(transactionId))
                return Fail("Το ePay αναφέρει έγκριση αλλά δεν επέστρεψε Transaction ID. Μην επαναλάβεις τη χρέωση πριν ελέγξεις το POS.", intent.Id);
            return new PaymentResult(true, transactionId, "Η συναλλαγή εγκρίθηκε από το ePay.", intent.Id);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return Fail("Έληξε ο χρόνος αναμονής από το ePay. Το αποτέλεσμα μπορεί να είναι άγνωστο· έλεγξε το POS πριν επιχειρήσεις νέα χρέωση.");
        }
        catch (HttpRequestException ex) { return Fail("Αδυναμία επικοινωνίας με ePay WebECR: " + ex.Message); }
        catch (JsonException) { return Fail("Το ePay επέστρεψε απάντηση σε μη αναμενόμενη μορφή."); }
        catch (Exception ex) { return Fail("Σφάλμα ePay WebECR: " + ex.Message); }
    }

    private static string BuildProviderSignatureDiagnostic(ProviderPaymentData? providerData, string terminalId, string backendId, int amountCents)
    {
        if (providerData is null) return "ProviderData was not sent.";
        var signatureFormat = providerData.Signature.StartsWith("30", StringComparison.OrdinalIgnoreCase)
            ? "ASN.1/DER HEX"
            : "HEX";
        return $"ProviderId={providerData.ProviderId}, TID={terminalId}, WebECR terminal ID URL={backendId}, " +
            $"Timestamp={providerData.SignatureTimestamp}, Net={providerData.NetAmount}, VAT={providerData.VatAmount}, " +
            $"Total={providerData.TotalAmount}, Amount={amountCents}, " +
            $"SignedAmounts='{providerData.SignedAmountTuple ?? "<not recorded>"}', " +
            $"SignedOrder='{providerData.SignedFieldOrder ?? "<not recorded>"}', " +
            $"SignatureLength={providerData.Signature.Length}, SignatureFormat={signatureFormat}.";
    }

    private static PaymentResult Fail(string message, string? intentId = null) => new(false, "", message, intentId);

    private static string AuthenticationError(string url, System.Net.HttpStatusCode code, string body, string? provider = null)
    {
        if (code == System.Net.HttpStatusCode.Forbidden &&
            body.Contains("blocked by our security service", StringComparison.OrdinalIgnoreCase))
        {
            string ReadValue(string name)
            {
                try
                {
                    using var document = JsonDocument.Parse(body);
                    return document.RootElement.TryGetProperty(name, out var value) ? value.ToString() : "";
                }
                catch (JsonException) { return ""; }
            }

            var incidentId = ReadValue("incidentId");
            var timeUtc = ReadValue("timeUtc");
            var clientIp = ReadValue("clientIp");
            var proxyId = ReadValue("proxyId");
            return $"Η ePay απέρριψε το POST /token/ με HTTP 403: το security service μπλόκαρε το αίτημα προς {url}. Δεν εκδόθηκε access token και δεν ξεκίνησε συναλλαγή ή χρέωση. Σταμάτησε τις επαναλήψεις και επικοινώνησε με την ePay για άρση/έλεγχο του αποκλεισμού. " +
                $"Incident ID: {incidentId}; ώρα UTC: {timeUtc}; client IP: {clientIp}; proxy ID: {proxyId}.";
        }

        return ApiError($"{CommonWebEcrProviderProfile.ProviderLabel(provider)} authentication POST {url}", code, body);
    }

    private static string ApiError(string action, System.Net.HttpStatusCode code, string body)
    {
        var safeBody = body.Length > 500 ? body[..500] : body;
        return $"{action}: HTTP {(int)code} ({code}). {safeBody}";
    }
}
