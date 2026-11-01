using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using RestaurantOrders.Data;
using RestaurantOrders.Models;

namespace RestaurantOrders.Services;

/// <summary>EDPS Middleware Chronos WebECR API v6 client.</summary>
public sealed class EdpsWebEcrService(
    IHttpClientFactory httpClientFactory,
    IDbContextFactory<AppDbContext> dbFactory)
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    public sealed record SetupResult(bool Success, string Message, string? ApiKey = null);
    public sealed record PaymentResult(
        bool Success,
        string TransactionId,
        string Message,
        string? IntentId = null,
        bool RetryRequiresFreshProviderData = false,
        bool RequiresStatusCheck = false);

    public async Task<SetupResult> RedeemAsync(
        BankPosTerminalSettings settings,
        CancellationToken cancellationToken = default)
    {
        if (settings.Id <= 0)
            return new(false, "Αποθήκευσε πρώτα τη ρύθμιση του EDPS POS.");
        if (!IsEdpsWebEcr(settings))
            return new(false, "Επίλεξε το EDPS Middleware (Chronos WebECR API v6).");
        if (!TryGetBaseUri(settings.Endpoint, out _, out var error))
            return new(false, error);
        if (string.IsNullOrWhiteSpace(settings.Username) || string.IsNullOrWhiteSpace(settings.Password))
            return new(false, "Συμπλήρωσε Username και Password του EDPS Web API.");
        if (string.IsNullOrWhiteSpace(settings.RedeemCode))
            return new(false, "Βάλε τον one-time κωδικό που εμφανίζει το POS.");

        var client = httpClientFactory.CreateClient("EDPS-WebECR");
        client.Timeout = TimeSpan.FromSeconds(30);
        client.DefaultRequestHeaders.Accept.Clear();
        client.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));

        try
        {
            var token = await GetAccessTokenAsync(client, settings, cancellationToken);
            if (!token.Success)
                return new(false, token.Error!);

            using var request = CreateJsonRequest(
                HttpMethod.Post,
                ResolveEndpoint(settings, settings.RedeemEndpoint, "authorization/redeem"),
                new { Type = "webecr", Code = settings.RedeemCode.Trim() },
                token.AccessToken,
                null);
            using var response = await client.SendAsync(request, cancellationToken);
            var body = await response.Content.ReadAsStringAsync(cancellationToken);
            if (!response.IsSuccessStatusCode)
                return new(false, BuildApiError("Redeem POS", response.StatusCode, body));

            var redeemed = JsonSerializer.Deserialize<RedeemResponse>(body, JsonOptions);
            if (string.IsNullOrWhiteSpace(redeemed?.Id))
                return new(false, "Το EDPS δεν επέστρεψε API key από το redeem.");

            using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
            var saved = await db.BankPosTerminalSettings.FirstOrDefaultAsync(x => x.Id == settings.Id, cancellationToken);
            if (saved is null)
                return new(false, "Η ρύθμιση EDPS δεν βρέθηκε στη βάση για αποθήκευση του API key.");

            saved.ApiKey = redeemed.Id;
            saved.RedeemCode = string.Empty;
            saved.UpdatedAt = DateTime.UtcNow;
            await db.SaveChangesAsync(cancellationToken);

            settings.ApiKey = redeemed.Id;
            settings.RedeemCode = string.Empty;
            return new(true, "Το POS έγινε redeem και το API key αποθηκεύτηκε.", redeemed.Id);
        }
        catch (TaskCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return new(false, "Έληξε το χρονικό όριο επικοινωνίας με το EDPS κατά το redeem.");
        }
        catch (HttpRequestException)
        {
            return new(false, "Δεν ήταν δυνατή η επικοινωνία με το EDPS. Έλεγξε Base URL, δίκτυο και περιβάλλον.");
        }
        catch (JsonException)
        {
            return new(false, "Το EDPS επέστρεψε απάντηση σε μη αναμενόμενη μορφή.");
        }
        catch (Exception ex)
        {
            return new(false, "Σφάλμα ρύθμισης EDPS: " + ex.Message);
        }
    }

    public async Task<PaymentResult> ChargeAsync(
        BankPosTerminalSettings settings,
        decimal amount,
        string customerReference,
        ProviderPaymentData? providerData,
        CancellationToken cancellationToken = default)
    {
        if (!settings.IsEnabled)
            return Fail("Το EDPS POS δεν είναι ενεργό στις Ρυθμίσεις → Τραπεζικά POS.");
        if (!IsEdpsWebEcr(settings))
            return Fail("Το επιλεγμένο τερματικό δεν είναι EDPS Middleware (Chronos WebECR API v6).");
        if (!TryGetBaseUri(settings.Endpoint, out _, out var baseError))
            return Fail(baseError);
        if (string.IsNullOrWhiteSpace(settings.Username) || string.IsNullOrWhiteSpace(settings.Password))
            return Fail("Συμπλήρωσε Username και Password του EDPS Web API.");
        if (string.IsNullOrWhiteSpace(settings.ApiKey))
            return Fail("Δεν υπάρχει αποθηκευμένο API key POS. Κάνε πρώτα Redeem στις Ρυθμίσεις → Τραπεζικά POS.");
        if (amount <= 0)
            return Fail("Το ποσό της συναλλαγής πρέπει να είναι μεγαλύτερο από μηδέν.");
        if (providerData is null || string.IsNullOrWhiteSpace(providerData.Uid) ||
            string.IsNullOrWhiteSpace(providerData.SignatureTimestamp) ||
            string.IsNullOrWhiteSpace(providerData.OriginalSignature ?? providerData.Signature))
            return Fail("Λείπουν τα υπογεγραμμένα ProviderData του Parochos. Δεν στάλθηκε χρέωση.");

        var amountCents = decimal.ToInt32(decimal.Round(amount * 100m, 0, MidpointRounding.AwayFromZero));
        if (amountCents != providerData.TotalAmount ||
            providerData.NetAmount + providerData.VatAmount != providerData.TotalAmount)
            return Fail("Το ποσό της συναλλαγής δεν συμφωνεί με τα υπογεγραμμένα ProviderData. Δεν στάλθηκε χρέωση.");

        // Web API v6 documents /terminal/txninit as the preferred route.
        // Use the pathless form by default; retain /terminal/{id}/txninit as an
        // explicit override for deployments that provide/use a route terminal ID.
        var configuredTerminalRouteId = settings.IdUrl?.Trim();

        var timeoutSeconds = Math.Clamp(settings.TimeoutSeconds, 30, 180);
        var client = httpClientFactory.CreateClient("EDPS-WebECR");
        client.Timeout = TimeSpan.FromSeconds(Math.Min(timeoutSeconds + 10, 190));
        client.DefaultRequestHeaders.Accept.Clear();
        client.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));

        string? accessTokenForReconciliation = null;
        string? customerReferenceForReconciliation = null;
        var txnInitRequestStarted = false;
        try
        {
            var token = await GetAccessTokenAsync(client, settings, cancellationToken);
            if (!token.Success)
                return Fail(token.Error!);
            accessTokenForReconciliation = token.AccessToken;

            var reference = string.IsNullOrWhiteSpace(customerReference)
                ? $"RO-{Guid.NewGuid():N}"
                : customerReference.Trim();
            if (reference.Length > 50)
                reference = reference[..50];
            customerReferenceForReconciliation = reference;

            var providerPayload = new Dictionary<string, object?>
            {
                ["Uid"] = providerData.Uid,
                ["Mark"] = providerData.Mark,
                ["SignatureTimestamp"] = providerData.SignatureTimestamp,
                ["NetAmount"] = providerData.NetAmount,
                ["VatAmount"] = providerData.VatAmount,
                ["TotalAmount"] = providerData.TotalAmount,
                ["Signature"] = providerData.OriginalSignature?.Trim() ?? providerData.Signature.Trim(),
                ["ProviderId"] = providerData.ProviderId
            };
            var payload = new Dictionary<string, object?>
            {
                ["TxnType"] = 0,
                ["Amount"] = amountCents,
                ["CashbackAmount"] = 0,
                ["CurrencyCode"] = 978,
                ["CustomerReference"] = reference,
                ["Timeout"] = timeoutSeconds,
                ["ProviderData"] = providerPayload
            };

            var txnPath = string.IsNullOrWhiteSpace(configuredTerminalRouteId)
                ? "terminal/txninit"
                : $"terminal/{Uri.EscapeDataString(configuredTerminalRouteId)}/txninit";
            var txnUrl = ResolveEndpoint(settings, null, txnPath);
            using var request = CreateJsonRequest(HttpMethod.Post, txnUrl, payload, token.AccessToken, settings.ApiKey.Trim());
            txnInitRequestStarted = true;
            using var response = await client.SendAsync(request, cancellationToken);
            var body = await response.Content.ReadAsStringAsync(cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                if ((int)response.StatusCode >= 500)
                {
                    var recovered = await TryReconcileAsync(client, settings, token.AccessToken!, reference);
                    if (recovered is not null)
                        return recovered;
                }
                if (IsProviderSignatureAlreadyUsed(body))
                {
                    var duplicateSignatureMessage =
                        "EDPS απέρριψε το αίτημα επειδή η υπογραφή Parochos έχει ήδη χρησιμοποιηθεί. " +
                        "Δεν ξεκίνησε νέα συναλλαγή από αυτό το αίτημα. Έλεγξε στο POS αν η προηγούμενη προσπάθεια χρεώθηκε. " +
                        "Αν επιβεβαιώσεις ότι απορρίφθηκε, πάτησε ξανά αποστολή· θα ζητηθεί νέα υπογραφή και θα σταλεί νέα προσπάθεια.";
                    return Fail(AddCustomerReference(duplicateSignatureMessage, reference),
                        requiresStatusCheck: true);
                }
                return Fail(AddCustomerReference(BuildApiError("Start transaction", response.StatusCode, body), reference),
                    requiresStatusCheck: (int)response.StatusCode >= 500);
            }

            var result = JsonSerializer.Deserialize<TxnInitResponse>(body, JsonOptions);
            if (result is null)
            {
                var recovered = await TryReconcileAsync(client, settings, token.AccessToken!, reference);
                return recovered ?? Fail(AddCustomerReference(
                    "Το EDPS επέστρεψε κενή απάντηση. Έλεγξε την κατάσταση στο POS πριν ξαναδοκιμάσεις.", reference),
                    requiresStatusCheck: true);
            }
            if (result.Result != 1 || result.Transaction?.Approved != true)
            {
                var definitiveDecline = (result.Result.HasValue && result.Result.Value != 1) ||
                    result.Transaction?.Approved == false;
                return Fail(BuildDeclinedPaymentMessage(result, definitiveDecline), result.Id,
                    retryRequiresFreshProviderData: definitiveDecline,
                    requiresStatusCheck: !definitiveDecline);
            }
            if (string.IsNullOrWhiteSpace(result.TransactionId))
            {
                var recovered = await TryReconcileAsync(client, settings, token.AccessToken!, reference);
                return recovered ?? Fail(
                    "Το EDPS αναφέρει έγκριση αλλά δεν επέστρεψε νόμιμο TransactionId. Έλεγξε την κατάσταση στο POS πριν ξαναδοκιμάσεις.",
                    result.Id, requiresStatusCheck: true);
            }

            return new(true, result.TransactionId!, "Η πληρωμή EDPS εγκρίθηκε.", result.Id);
        }
        catch (TaskCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            var recovered = await TryReconcileAsync(client, settings, accessTokenForReconciliation,
                customerReferenceForReconciliation);
            return recovered ?? Fail(AddCustomerReference(
                "Έληξε το χρονικό όριο αναμονής του EDPS. Έλεγξε την κατάσταση στο POS πριν ξαναδοκιμάσεις.",
                customerReferenceForReconciliation), requiresStatusCheck: txnInitRequestStarted);
        }
        catch (HttpRequestException ex)
        {
            var recovered = await TryReconcileAsync(client, settings, accessTokenForReconciliation,
                customerReferenceForReconciliation);
            var safeDetails = new List<string>();
            AddSafeResponseDetails(safeDetails, ex.Message);
            var networkDetail = safeDetails.Count == 0 ? "" : $" Τεχνική λεπτομέρεια: {safeDetails[0]}.";
            return recovered ?? Fail(AddCustomerReference(
                "Χάθηκε η επικοινωνία με το EDPS κατά την έναρξη συναλλαγής." + networkDetail +
                " Έλεγξε την κατάσταση στο POS πριν ξαναδοκιμάσεις.",
                customerReferenceForReconciliation), requiresStatusCheck: txnInitRequestStarted);
        }
        catch (JsonException)
        {
            var recovered = await TryReconcileAsync(client, settings, accessTokenForReconciliation,
                customerReferenceForReconciliation);
            return recovered ?? Fail(AddCustomerReference(
                "Το EDPS επέστρεψε απάντηση σε μη αναμενόμενη μορφή. Έλεγξε την κατάσταση στο POS πριν ξαναδοκιμάσεις.",
                customerReferenceForReconciliation), requiresStatusCheck: txnInitRequestStarted);
        }
        catch (Exception)
        {
            var recovered = await TryReconcileAsync(client, settings, accessTokenForReconciliation,
                customerReferenceForReconciliation);
            return recovered ?? Fail("Σφάλμα επικοινωνίας EDPS Middleware. Έλεγξε την κατάσταση στο POS πριν ξαναδοκιμάσεις.",
                requiresStatusCheck: txnInitRequestStarted);
        }
    }

    private async Task<TokenResult> GetAccessTokenAsync(
        HttpClient client,
        BankPosTerminalSettings settings,
        CancellationToken cancellationToken)
    {
        using var request = CreateJsonRequest(
            HttpMethod.Post,
            ResolveEndpoint(settings, settings.AuthEndpoint, "token"),
            new { Username = settings.Username.Trim(), Password = settings.Password },
            null,
            string.IsNullOrWhiteSpace(settings.ApiSecret) ? null : settings.ApiSecret.Trim());
        using var response = await client.SendAsync(request, cancellationToken);
        var body = await response.Content.ReadAsStringAsync(cancellationToken);
        if (!response.IsSuccessStatusCode)
            return new(null, BuildApiError("Authenticate", response.StatusCode, body));

        var token = JsonSerializer.Deserialize<TokenResponse>(body, JsonOptions);
        return string.IsNullOrWhiteSpace(token?.Access)
            ? new(null, "Το EDPS δεν επέστρεψε access token. Έλεγξε credentials, API key και περιβάλλον.")
            : new(token.Access, null);
    }

    private async Task<PaymentResult?> TryReconcileAsync(
        HttpClient client,
        BankPosTerminalSettings settings,
        string? accessToken,
        string? customerReference)
    {
        if (string.IsNullOrWhiteSpace(accessToken) || string.IsNullOrWhiteSpace(customerReference) ||
            string.IsNullOrWhiteSpace(settings.ApiKey))
            return null;

        // txninit may reach the POS even when its HTTP response is lost. Search by
        // the unique reference; never submit the payment again from this recovery path.
        var query = "transactionintent?TID=" + Uri.EscapeDataString(settings.TerminalId.Trim()) +
            "&CustomerReference=" + Uri.EscapeDataString(customerReference) + "&Page=0&Size=10";
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(8));
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, ResolveEndpoint(settings, null, query));
            request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
            request.Headers.TryAddWithoutValidation("X-Api-Key", settings.ApiKey.Trim());
            using var response = await client.SendAsync(request, timeout.Token);
            if (!response.IsSuccessStatusCode)
                return null;

            var body = await response.Content.ReadAsStringAsync(timeout.Token);
            var list = JsonSerializer.Deserialize<TransactionIntentListResponse>(body, JsonOptions);
            var matching = list?.Results?.Where(x => string.Equals(
                x.CustomerReference, customerReference, StringComparison.Ordinal)).Take(2).ToArray();
            if (matching is not { Length: 1 })
                return null;

            var intent = matching[0];
            if (intent.Result == 1 && intent.Transaction?.Approved == true &&
                !string.IsNullOrWhiteSpace(intent.TransactionId))
                return new(true, intent.TransactionId!,
                    "Η πληρωμή EDPS εγκρίθηκε και επιβεβαιώθηκε από αναζήτηση κατάστασης.", intent.Id);

            var definitiveDecline = (intent.Result.HasValue && intent.Result.Value != 1) ||
                intent.Transaction?.Approved == false;
            return Fail(AddCustomerReference(BuildDeclinedPaymentMessage(intent, definitiveDecline), customerReference),
                intent.Id, definitiveDecline, !definitiveDecline);
        }
        catch (HttpRequestException)
        {
            return null;
        }
        catch (TaskCanceledException)
        {
            return null;
        }
        catch (JsonException)
        {
            return null;
        }
        catch (InvalidOperationException)
        {
            return null;
        }
    }

    private static string AddCustomerReference(string message, string? customerReference) =>
        string.IsNullOrWhiteSpace(customerReference)
            ? message
            : message + $" EDPS CustomerReference: {customerReference}.";

    private static HttpRequestMessage CreateJsonRequest(
        HttpMethod method,
        Uri uri,
        object body,
        string? accessToken,
        string? apiKey)
    {
        var request = new HttpRequestMessage(method, uri)
        {
            Content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json")
        };
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        if (!string.IsNullOrWhiteSpace(accessToken))
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
        if (!string.IsNullOrWhiteSpace(apiKey))
            request.Headers.TryAddWithoutValidation("X-Api-Key", apiKey);
        return request;
    }

    private static Uri ResolveEndpoint(BankPosTerminalSettings settings, string? overrideUrl, string relativePath)
    {
        if (!string.IsNullOrWhiteSpace(overrideUrl))
        {
            var configured = overrideUrl.Trim();
            if (Uri.TryCreate(configured, UriKind.Absolute, out var absolute))
                return absolute;
            var baseUri = new Uri(settings.Endpoint.TrimEnd('/') + "/", UriKind.Absolute);
            return new Uri(baseUri, configured.TrimStart('/'));
        }

        var endpointBase = new Uri(settings.Endpoint.TrimEnd('/') + "/", UriKind.Absolute);
        return new Uri(endpointBase, relativePath.TrimStart('/'));
    }

    private static bool TryGetBaseUri(string? endpoint, out Uri baseUri, out string error)
    {
        baseUri = null!;
        error = "Το EDPS Base URL δεν είναι έγκυρο HTTP/HTTPS URL.";
        if (!Uri.TryCreate((endpoint ?? "").Trim().TrimEnd('/') + "/", UriKind.Absolute, out var uri) ||
            (uri.Scheme != Uri.UriSchemeHttps && uri.Scheme != Uri.UriSchemeHttp))
            return false;
        baseUri = uri;
        error = "";
        return true;
    }

    private static bool IsEdpsWebEcr(BankPosTerminalSettings settings) =>
        settings.Provider.Equals("EDPS", StringComparison.OrdinalIgnoreCase) &&
        settings.Protocol.Contains("WebECR API v6", StringComparison.OrdinalIgnoreCase);

    private static string BuildApiError(string operation, HttpStatusCode statusCode, string? body = null)
    {
        var detail = ExtractSafeErrorMessage(body);
        var authHint = statusCode == HttpStatusCode.Unauthorized
            ? " Έλεγξε credentials, API key και ότι το POS έχει γίνει Redeem στο ίδιο περιβάλλον."
            : " Έλεγξε ρυθμίσεις και περιβάλλον.";
        return $"EDPS {operation}: HTTP {(int)statusCode}." +
            (string.IsNullOrWhiteSpace(detail) ? "" : " " + detail) + authHint;
    }

    private static string? ExtractSafeErrorMessage(string? body)
    {
        if (string.IsNullOrWhiteSpace(body)) return null;
        try
        {
            using var document = JsonDocument.Parse(body);
            var root = document.RootElement;
            var messages = new List<string>();
            if (root.TryGetProperty("Errors", out var errors) && errors.ValueKind == JsonValueKind.Array)
            {
                foreach (var error in errors.EnumerateArray())
                    if (error.ValueKind == JsonValueKind.Object && error.TryGetProperty("Message", out var message) &&
                        message.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(message.GetString()))
                        messages.Add(message.GetString()!);
            }
            if (messages.Count == 0 && root.ValueKind == JsonValueKind.Object)
            {
                foreach (var propertyName in new[] { "Message", "error", "Error" })
                    if (root.TryGetProperty(propertyName, out var value) && value.ValueKind == JsonValueKind.String &&
                        !string.IsNullOrWhiteSpace(value.GetString()))
                    {
                        messages.Add(value.GetString()!);
                        break;
                    }
            }
            if (messages.Count == 0) return null;
            var detail = RedactSensitiveDetails(string.Join("; ", messages));
            return detail.Length > 300 ? detail[..300] : detail;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static string BuildDeclinedPaymentMessage(TxnInitResponse result, bool retryCanUseFreshProviderData = false)
    {
        var details = new List<string>();
        AddSafeResponseDetails(details, result.ErrorMessage);
        AddSafeResponseFields(details, result.AdditionalData);
        AddSafeResponseFields(details, result.Transaction?.AdditionalData);

        var message = "Η συναλλαγή EDPS δεν εγκρίθηκε.";
        if (result.Result.HasValue)
            message += $" Result={result.Result.Value}.";
        if (details.Count > 0)
            message += " Απάντηση EDPS: " + string.Join("; ", details.Distinct()) + ".";
        if (!string.IsNullOrWhiteSpace(result.Id))
            message += $" EDPS Intent ID: {result.Id}.";
        if (retryCanUseFreshProviderData)
            message += " Για νέα προσπάθεια, πάτησε ξανά «Αποστολή ποσού στο EDPS POS»· θα ζητηθεί αυτόματα νέα υπογραφή Parochos.";
        return message;
    }

    private static void AddSafeResponseFields(List<string> details, Dictionary<string, JsonElement>? fields)
    {
        if (fields is null) return;
        var safeFieldNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "ErrorCode", "ResponseCode", "Code", "ErrorMessage", "ResponseMessage", "Description", "DeclineReason", "Message"
        };
        foreach (var field in fields)
        {
            if (!safeFieldNames.Contains(field.Key)) continue;
            var value = field.Value.ValueKind switch
            {
                JsonValueKind.String => field.Value.GetString(),
                JsonValueKind.Number => field.Value.GetRawText(),
                JsonValueKind.True => "true",
                JsonValueKind.False => "false",
                _ => null
            };
            AddSafeResponseDetails(details, value);
        }
    }

    private static void AddSafeResponseDetails(List<string> details, string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return;
        var safe = RedactSensitiveDetails(value.Trim());
        safe = Regex.Replace(safe, @"(?<!\d)(?:\d[ -]?){12,19}(?!\d)", "[card redacted]");
        if (safe.Length > 200) safe = safe[..200];
        if (!details.Contains(safe, StringComparer.OrdinalIgnoreCase)) details.Add(safe);
    }

    private static bool IsProviderSignatureAlreadyUsed(string? body)
    {
        if (string.IsNullOrWhiteSpace(body)) return false;
        var safeMessage = ExtractSafeErrorMessage(body) ?? body;
        return safeMessage.Contains("acquirer signature", StringComparison.OrdinalIgnoreCase) &&
            safeMessage.Contains("already exists", StringComparison.OrdinalIgnoreCase);
    }

    private static string RedactSensitiveDetails(string value)
    {
        var safe = Regex.Replace(value,
            "(?i)(password|token|secret|api[-_ ]?key)\\s*[:=]\\s*[^,;\\s]+", "$1=[redacted]");
        return Regex.Replace(safe,
            "(?i)((?:acquirer\\s+|provider\\s+|parochos\\s+)?signature(?:\\s*[:=]\\s*|\\s+))[A-Za-z0-9+/=_-]{16,}",
            "$1[redacted]");
    }

    private static PaymentResult Fail(
        string message,
        string? intentId = null,
        bool retryRequiresFreshProviderData = false,
        bool requiresStatusCheck = false) =>
        new(false, "", message, intentId, retryRequiresFreshProviderData, requiresStatusCheck);

    private sealed record TokenResult(string? AccessToken, string? Error)
    {
        public bool Success => !string.IsNullOrWhiteSpace(AccessToken);
    }

    private sealed record TokenResponse(string? Access, string? Refresh);
    private sealed record RedeemResponse(string? Id, string? Type);
    private sealed class TxnInitResponse
    {
        public int? Status { get; set; }
        public int? Result { get; set; }
        public string? Id { get; set; }
        public string? TransactionId { get; set; }
        public string? ErrorMessage { get; set; }
        public TxnResponse? Transaction { get; set; }
        public string? CustomerReference { get; set; }
        [JsonExtensionData] public Dictionary<string, JsonElement>? AdditionalData { get; set; }
    }

    private sealed class TxnResponse
    {
        public string? Id { get; set; }
        public bool? Approved { get; set; }
        public string? RRN { get; set; }
        public string? AuthCode { get; set; }
        [JsonExtensionData] public Dictionary<string, JsonElement>? AdditionalData { get; set; }
    }

    private sealed class TransactionIntentListResponse
    {
        public List<TxnInitResponse>? Results { get; set; }
        [JsonExtensionData] public Dictionary<string, JsonElement>? AdditionalData { get; set; }
    }
}
