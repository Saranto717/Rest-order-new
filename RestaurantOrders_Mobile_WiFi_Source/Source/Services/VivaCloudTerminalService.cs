using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using RestaurantOrders.Models;

namespace RestaurantOrders.Services;

/// <summary>Viva Cloud Terminal (EFT POS) REST API client. Uses merchant POS API OAuth credentials.</summary>
public sealed class VivaCloudTerminalService(IHttpClientFactory httpClientFactory)
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        PropertyNameCaseInsensitive = true
    };

    public sealed record TerminalChoice(string TerminalId, int StatusId, string? SourceCode, string? VirtualTerminalId);
    public sealed record OperationResult(bool Success, string Message, string? TransactionId = null,
        string? SessionId = null, bool RequiresStatusCheck = false, string? AuthorizationId = null);
    private sealed record TokenResponse([property: JsonPropertyName("access_token")] string? AccessToken,
        [property: JsonPropertyName("expires_in")] int ExpiresIn);
    private sealed record DeviceDto(string? TerminalId, int StatusId, string? SourceCode, string? VirtualTerminalId);
    private sealed record SessionDto(string? SessionId, string? TerminalId, string? CashRegisterId,
        bool? Success, int? EventId, int? TransactionEventId, string? TransactionId,
        string? AuthorizationId, string? CardType, string? PrimaryAccountNumberMasked,
        string? TransactionDateTime, string? Message);

    private static string DefaultApiBase(bool production) => production
        ? "https://api.vivapayments.com/"
        : "https://demo-api.vivapayments.com/";

    private static string DefaultTokenUrl(bool production) => production
        ? "https://accounts.vivapayments.com/connect/token"
        : "https://demo-accounts.vivapayments.com/connect/token";

    // Accept either the API root or the complete /ecr/v1 base shown by some provider applications.
    private static string NormalizeApiBase(string value)
    {
        var normalized = value.Trim().TrimEnd('/');
        if (normalized.EndsWith("/ecr/v1", StringComparison.OrdinalIgnoreCase))
            normalized = normalized[..^"/ecr/v1".Length];
        return normalized + "/";
    }

    private static bool IsValidHttpUrl(string value) => Uri.TryCreate(value, UriKind.Absolute, out var uri) &&
        (uri.Scheme == Uri.UriSchemeHttps || uri.Scheme == Uri.UriSchemeHttp);

    private async Task<(HttpClient? Client, string Error)> CreateAuthenticatedClientAsync(
        BankPosTerminalSettings settings, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(settings.ApiKey) || string.IsNullOrWhiteSpace(settings.ApiSecret))
            return (null, "Συμπλήρωσε τα Viva POS APIs Client ID και Client Secret.");
        var apiBase = string.IsNullOrWhiteSpace(settings.Endpoint)
            ? DefaultApiBase(settings.UseProduction)
            : NormalizeApiBase(settings.Endpoint);
        var tokenUrl = string.IsNullOrWhiteSpace(settings.AuthEndpoint)
            ? DefaultTokenUrl(settings.UseProduction)
            : settings.AuthEndpoint.Trim();
        if (!IsValidHttpUrl(apiBase) || !IsValidHttpUrl(tokenUrl))
            return (null, "Το Viva Base URL ή Auth URL δεν είναι έγκυρο HTTP/HTTPS URL.");

        var client = httpClientFactory.CreateClient("Viva-Cloud-Terminal");
        client.BaseAddress = new Uri(apiBase);
        client.Timeout = TimeSpan.FromSeconds(Math.Clamp(settings.TimeoutSeconds, 15, 180));
        client.DefaultRequestHeaders.Accept.Clear();
        client.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, tokenUrl);
            var credentials = Convert.ToBase64String(Encoding.UTF8.GetBytes(settings.ApiKey.Trim() + ":" + settings.ApiSecret));
            request.Headers.Authorization = new AuthenticationHeaderValue("Basic", credentials);
            request.Content = new FormUrlEncodedContent(new Dictionary<string, string> { ["grant_type"] = "client_credentials", ["scope"] = "urn:viva:payments:ecr:api" });
            using var response = await client.SendAsync(request, cancellationToken);
            var body = await response.Content.ReadAsStringAsync(cancellationToken);
            if (!response.IsSuccessStatusCode)
                return (null, $"Viva OAuth απέτυχε ({(int)response.StatusCode}): {TrimBody(body)}");
            var token = JsonSerializer.Deserialize<TokenResponse>(body, JsonOptions);
            if (string.IsNullOrWhiteSpace(token?.AccessToken)) return (null, "Η Viva δεν επέστρεψε access_token.");
            client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token.AccessToken);
            return (client, "");
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        { return (null, "Έληξε ο χρόνος αναμονής κατά το Viva OAuth."); }
        catch (HttpRequestException ex) { return (null, "Αδυναμία επικοινωνίας με Viva: " + ex.Message); }
        catch (JsonException) { return (null, "Η Viva επέστρεψε μη αναγνωρίσιμη απάντηση OAuth."); }
    }

    public async Task<(bool Success, string Message, IReadOnlyList<TerminalChoice> Terminals)> GetLiveTerminalsAsync(
        BankPosTerminalSettings settings, CancellationToken cancellationToken = default)
    {
        var (client, error) = await CreateAuthenticatedClientAsync(settings, cancellationToken);
        if (client is null) return (false, error, Array.Empty<TerminalChoice>());
        using (client)
        {
            try
            {
                using var response = await client.PostAsJsonAsync("ecr/v1/devices:search", new { statusId = 1 }, JsonOptions, cancellationToken);
                var body = await response.Content.ReadAsStringAsync(cancellationToken);
                if (!response.IsSuccessStatusCode)
                    return (false, $"Viva αναζήτηση POS απέτυχε ({(int)response.StatusCode}): {TrimBody(body)}", Array.Empty<TerminalChoice>());
                var devices = JsonSerializer.Deserialize<List<DeviceDto>>(body, JsonOptions) ?? new();
                var terminals = devices.Where(x => !string.IsNullOrWhiteSpace(x.TerminalId))
                    .Select(x => new TerminalChoice(x.TerminalId!, x.StatusId, x.SourceCode, x.VirtualTerminalId)).ToList();
                return (true, terminals.Count == 0 ? "Η σύνδεση πέτυχε, αλλά δεν βρέθηκαν POS σε κατάσταση Live." : $"Βρέθηκαν {terminals.Count} ενεργά Viva POS.", terminals);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            { return (false, "Έληξε ο χρόνος αναμονής κατά την αναζήτηση Viva POS.", Array.Empty<TerminalChoice>()); }
            catch (HttpRequestException ex) { return (false, "Αδυναμία αναζήτησης Viva POS: " + ex.Message, Array.Empty<TerminalChoice>()); }
            catch (JsonException) { return (false, "Η Viva επέστρεψε μη αναγνωρίσιμη λίστα τερματικών.", Array.Empty<TerminalChoice>()); }
        }
    }

    public async Task<OperationResult> ChargeAsync(BankPosTerminalSettings settings, decimal amount,
        string merchantReference, ProviderPaymentData providerData, CancellationToken cancellationToken = default)
    {
        if (!settings.IsEnabled) return new(false, "Το Viva POS δεν είναι ενεργό.");
        if (string.IsNullOrWhiteSpace(settings.TerminalId)) return new(false, "Συμπλήρωσε το Viva Terminal ID.");
        // Viva calls this cashRegisterId. Keep it separate from the physical terminal ID.
        if (string.IsNullOrWhiteSpace(settings.ProviderCode)) return new(false, "Συμπλήρωσε το Viva Cash Register ID στο πεδίο Provider Code.");
        if (amount <= 0) return new(false, "Το ποσό πληρωμής πρέπει να είναι μεγαλύτερο από μηδέν.");
        if (providerData is null || string.IsNullOrWhiteSpace(providerData.OriginalSignature))
            return new(false, "Λείπει η αρχική Base64 υπογραφή Parochos. Δεν ξεκίνησε χρέωση.");
        if (!TryGetVivaBase64Signature(providerData.OriginalSignature, out var vivaSignature))
            return new(false, "Η αρχική υπογραφή Parochos δεν είναι έγκυρη ECDSA P-256 ASN.1/DER. Δεν ξεκίνησε χρέωση.");
        if (amount != decimal.Round(amount, 2, MidpointRounding.AwayFromZero)) return new(false, "Το ποσό πρέπει να έχει έως δύο δεκαδικά ψηφία.");

        var (client, error) = await CreateAuthenticatedClientAsync(settings, cancellationToken);
        if (client is null) return new(false, error);
        using (client)
        {
            // Viva recommends sending requests only to terminals currently reported as Live.
            try
            {
                using var devicesResponse = await client.PostAsJsonAsync("ecr/v1/devices:search", new { statusId = 1 }, JsonOptions, cancellationToken);
                var devicesBody = await devicesResponse.Content.ReadAsStringAsync(cancellationToken);
                if (!devicesResponse.IsSuccessStatusCode)
                    return new(false, $"Δεν επιβεβαιώθηκε η κατάσταση του Viva POS ({(int)devicesResponse.StatusCode}): {TrimBody(devicesBody)}");
                var devices = JsonSerializer.Deserialize<List<DeviceDto>>(devicesBody, JsonOptions) ?? new();
                if (!devices.Any(x => string.Equals(x.TerminalId?.Trim(), settings.TerminalId.Trim(), StringComparison.OrdinalIgnoreCase) && x.StatusId == 1))
                    return new(false, "Το επιλεγμένο Viva POS δεν εμφανίζεται σε κατάσταση Live. Δεν ξεκίνησε χρέωση.");
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested) { return new(false, "Έληξε ο χρόνος ελέγχου κατάστασης του Viva POS. Δεν ξεκίνησε χρέωση."); }
            catch (HttpRequestException ex) { return new(false, "Δεν ήταν δυνατός ο έλεγχος κατάστασης του Viva POS. Δεν ξεκίνησε χρέωση. " + ex.Message); }
            catch (JsonException) { return new(false, "Η Viva επέστρεψε μη αναγνωρίσιμη κατάσταση τερματικού. Δεν ξεκίνησε χρέωση."); }

            var sessionId = Guid.NewGuid().ToString();
            var cents = decimal.ToInt64(decimal.Round(amount * 100m, 0, MidpointRounding.AwayFromZero));
            var signatureData = string.Join(";", new[]
            {
                providerData.Uid,
                providerData.Mark?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "",
                providerData.SignatureTimestamp,
                cents.ToString(System.Globalization.CultureInfo.InvariantCulture),
                providerData.NetAmount.ToString(System.Globalization.CultureInfo.InvariantCulture),
                providerData.VatAmount.ToString(System.Globalization.CultureInfo.InvariantCulture),
                providerData.TotalAmount.ToString(System.Globalization.CultureInfo.InvariantCulture),
                settings.TerminalId.Trim()
            });
            if (signatureData.Length > 200)
                return new(false, "Τα δεδομένα υπογραφής Parochos υπερβαίνουν το όριο 200 χαρακτήρων της Viva. Δεν ξεκίνησε χρέωση.");

            var payload = new Dictionary<string, object?>
            {
                ["sessionId"] = sessionId,
                ["terminalId"] = settings.TerminalId.Trim(),
                ["cashRegisterId"] = settings.ProviderCode.Trim(),
                ["amount"] = cents,
                // Viva requires tipAmount in cents even when the merchant does not accept tips.
                ["tipAmount"] = 0,
                ["currencyCode"] = "978",
                ["merchantReference"] = string.IsNullOrWhiteSpace(merchantReference) ? sessionId : merchantReference,
                ["customerTrns"] = string.IsNullOrWhiteSpace(merchantReference) ? sessionId : merchantReference,
                ["preauth"] = false,
                ["paymentMethod"] = "CardPresent",
                ["showTransactionResult"] = true,
                ["showReceipt"] = true,
                ["interappCallback"] = "finish",
                ["aadeProviderId"] = providerData.ProviderId.ToString("D3", System.Globalization.CultureInfo.InvariantCulture),
                ["aadeProviderSignatureData"] = signatureData,
                ["aadeProviderSignature"] = vivaSignature
            };

            try
            {
                using var response = await client.PostAsJsonAsync("ecr/v1/transactions:sale", payload, JsonOptions, cancellationToken);
                var responseBody = await response.Content.ReadAsStringAsync(cancellationToken);
                if (!response.IsSuccessStatusCode)
                {
                    var eventId = response.Headers.TryGetValues("X-viva-eventid", out var eventValues) ? eventValues.FirstOrDefault() : null;
                    var knownNotStarted = response.StatusCode is System.Net.HttpStatusCode.BadRequest or System.Net.HttpStatusCode.Unauthorized;
                    return new(false, $"Viva δεν αποδέχθηκε το αίτημα ({(int)response.StatusCode}, Event {eventId ?? "—"}): {TrimBody(responseBody)}", null, sessionId, !knownNotStarted);
                }

                // A successful HTTP response means the session was accepted, not that the card was charged.
                var deadline = DateTimeOffset.UtcNow.AddSeconds(Math.Clamp(settings.TimeoutSeconds, 30, 180));
                while (DateTimeOffset.UtcNow < deadline)
                {
                    await Task.Delay(TimeSpan.FromSeconds(2), cancellationToken);
                    using var statusResponse = await client.GetAsync("ecr/v1/sessions/" + Uri.EscapeDataString(sessionId), cancellationToken);
                    var statusBody = await statusResponse.Content.ReadAsStringAsync(cancellationToken);
                    if (statusResponse.StatusCode == System.Net.HttpStatusCode.Accepted) continue;
                    if (statusResponse.StatusCode == System.Net.HttpStatusCode.NotFound) continue;
                    if (!statusResponse.IsSuccessStatusCode)
                        return new(false, $"Η Viva δέχθηκε τη συναλλαγή αλλά δεν επιβεβαιώθηκε το αποτέλεσμά της ({(int)statusResponse.StatusCode}). Έλεγξε το ιστορικό POS πριν από νέα προσπάθεια.", null, sessionId, true);
                    var status = JsonSerializer.Deserialize<SessionDto>(statusBody, JsonOptions);
                    if (status is null) continue;
                    if (status.Success == true && !string.IsNullOrWhiteSpace(status.TransactionId))
                        return new(true, $"Η Viva ενέκρινε τη συναλλαγή. Transaction ID: {status.TransactionId}", status.TransactionId, sessionId, false, status.AuthorizationId);
                    if (status.Success == false && status.EventId is not null && status.EventId != 1204)
                        return new(false, $"Η Viva δεν ενέκρινε τη συναλλαγή (Event {status.EventId}). Δεν καταχωρίστηκε πληρωμή.", status.TransactionId, sessionId, false, status.AuthorizationId);
                }
                return new(false, "Η Viva δεν επέστρεψε τελικό αποτέλεσμα μέσα στο χρονικό όριο. Η χρέωση μπορεί να έχει ολοκληρωθεί· έλεγξε το ιστορικό POS πριν επιχειρήσεις ξανά.", null, sessionId, true);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            { return new(false, "Έληξε ο χρόνος αναμονής. Έλεγξε το ιστορικό Viva POS πριν από νέα προσπάθεια.", null, sessionId, true); }
            catch (HttpRequestException ex)
            { return new(false, "Διακόπηκε η επικοινωνία με τη Viva μετά την έναρξη της προσπάθειας. Έλεγξε το ιστορικό POS πριν από νέα προσπάθεια. " + ex.Message, null, sessionId, true); }
            catch (JsonException)
            { return new(false, "Η Viva επέστρεψε μη αναγνωρίσιμη κατάσταση. Έλεγξε το ιστορικό POS πριν από νέα προσπάθεια.", null, sessionId, true); }
        }
    }


    private static bool TryGetVivaBase64Signature(string signature, out string base64Signature)
    {
        base64Signature = "";
        var value = signature.Trim();
        byte[] bytes;
        try
        {
            if (value.Length > 0 && value.Length % 2 == 0 && value.All(Uri.IsHexDigit))
                bytes = Convert.FromHexString(value);
            else
            {
                var normalized = value.Replace('-', '+').Replace('_', '/');
                if (normalized.Length % 4 != 0) normalized = normalized.PadRight(normalized.Length + (4 - normalized.Length % 4), '=');
                bytes = Convert.FromBase64String(normalized);
            }
        }
        catch (FormatException) { return false; }
        if (bytes.Length is < 70 or > 72 || bytes[0] != 0x30 || bytes[1] != bytes.Length - 2)
            return false;
        base64Signature = Convert.ToBase64String(bytes);
        return true;
    }

    private static string TrimBody(string body)
    {
        var value = body?.Trim() ?? "";
        return value.Length <= 500 ? value : value[..500];
    }
}
