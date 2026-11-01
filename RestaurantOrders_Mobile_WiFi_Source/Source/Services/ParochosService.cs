using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using RestaurantOrders.Models;

namespace RestaurantOrders.Services;

public sealed class ParochosSendResult
{
    public bool Success { get; init; }
    public bool InProgress { get; init; }
    public string? ProcessId { get; init; }
    public long? Mark { get; init; }
    public string? Uid { get; init; }
    public string? AuthenticationCode { get; init; }
    public string? QrCode { get; init; }
    public string? PdfFileUrl { get; init; }
    public string? ErrorCategory { get; init; }
    public string? ErrorCode { get; init; }
    public string? ErrorMessage { get; init; }
}

public sealed class ParochosPaymentResult
{
    public bool Success { get; init; }
    public string? ExternalSystemId { get; init; }
    public string? ProcessId { get; init; }
    public string? Uid { get; init; }
    public string? Signature { get; init; }
    public string? SignatureClearText { get; init; }
    public decimal Amount { get; init; }
    public DateTimeOffset? Timestamp { get; init; }
    public string? ErrorCategory { get; init; }
    public string? ErrorCode { get; init; }
    public string? ErrorMessage { get; init; }
}

public sealed class ParochosSimpleResult
{
    public bool Success { get; init; }
    public int? ConnectedCount { get; init; }
    public string? ErrorCategory { get; init; }
    public string? ErrorCode { get; init; }
    public string? ErrorMessage { get; init; }
}

public sealed class ParochosPointOfServiceDetail
{
    public string? ExternalSystemId { get; init; }
    public long? Mark { get; init; }
    public string? InvoiceType { get; init; }
    public decimal? TotalAmount { get; init; }
    public decimal? TransactionAmount { get; init; }
    public string? InvoiceCode { get; init; }
    public int? OrderFormStatus { get; init; }
}

public sealed class ParochosPointOfServiceResult
{
    public bool Success { get; init; }
    public string? PointOfService { get; init; }
    public decimal? Balance { get; init; }
    public int? RefNumber { get; init; }
    public List<ParochosPointOfServiceDetail> Details { get; init; } = new();
    public string? ErrorCategory { get; init; }
    public string? ErrorCode { get; init; }
    public string? ErrorMessage { get; init; }
}

/// <summary>
/// Parochos API v3 client. Uses the url1 returned by login and always sends X-Version: 3.0.
/// The API documentation explicitly requires the url1 to be used dynamically and not hard-coded.
/// </summary>
public sealed class ParochosService
{
    private readonly HttpClient _http;
    private readonly ILogger<ParochosService> _logger;
    private readonly SemaphoreSlim _authLock = new(1, 1);

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true
    };

    private string? _jwt;
    private DateTime _jwtExpirationUtc;
    private string? _refreshToken;
    private DateTime _refreshExpirationUtc;
    private string? _baseUrl;
    private string? _sessionKey;

    public ParochosService(IHttpClientFactory httpClientFactory, ILogger<ParochosService> logger)
    {
        _http = httpClientFactory.CreateClient("Parochos");
        _logger = logger;
    }

    private static string AccountBaseUrl(bool production) =>
        production ? "https://account.parochos.gr" : "https://beta-account.parochos.gr";

    public async Task<(bool Success, string? Error)> TestConnectionAsync(CompanySettings settings)
    {
        try
        {
            await LoginAsync(settings);
            return (true, null);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Αποτυχία δοκιμαστικής σύνδεσης με Parochos");
            return (false, ex.Message);
        }
    }

    private string BuildSessionKey(CompanySettings settings) =>
        $"{settings.ParochosUseProduction}|{settings.ParochosSubscriptionKey}|{settings.ParochosUsername}";

    private async Task EnsureAuthenticatedAsync(CompanySettings settings)
    {
        var sessionKey = BuildSessionKey(settings);

        await _authLock.WaitAsync();

        try
        {
            if (_sessionKey != sessionKey)
            {
                _jwt = null;
                _refreshToken = null;
                _baseUrl = null;
                _sessionKey = sessionKey;

                _jwtExpirationUtc = DateTime.MinValue;
                _refreshExpirationUtc = DateTime.MinValue;
            }

            // Αν έχουμε έγκυρο JWT, το χρησιμοποιούμε.
            if (!string.IsNullOrWhiteSpace(_jwt) &&
                _jwtExpirationUtc > DateTime.UtcNow.AddMinutes(1))
            {
                return;
            }

            // Parochos docs (Refresh Token): όταν λήξει το JWT καλείται /api/token/Refresh
            // με το ληγμένο JWT και το refresh token. Αν αποτύχει ή έχει λήξει -> νέο login.
            if (!string.IsNullOrWhiteSpace(_jwt) &&
                !string.IsNullOrWhiteSpace(_refreshToken) &&
                !string.IsNullOrWhiteSpace(_baseUrl) &&
                _refreshExpirationUtc > DateTime.UtcNow.AddMinutes(1))
            {
                try
                {
                    await RefreshAsync(settings);
                    return;
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Parochos refresh απέτυχε. Γίνεται νέο login.");
                }
            }

            await LoginAsync(settings);
        }
        finally
        {
            _authLock.Release();
        }
    }

    private async Task LoginAsync(CompanySettings settings)
    {
        if (string.IsNullOrWhiteSpace(settings.ParochosSubscriptionKey) ||
            string.IsNullOrWhiteSpace(settings.ParochosUsername) ||
            string.IsNullOrWhiteSpace(settings.ParochosPassword))
        {
            throw new InvalidOperationException(
                "Συμπλήρωσε Subscription Key, API User και API Password του Parochos.");
        }

        var url =
            $"{AccountBaseUrl(settings.ParochosUseProduction)}/api/account/loginToSubscription";

        var payload = new
        {
            subscriptionKey = settings.ParochosSubscriptionKey.Trim(),
            email = settings.ParochosUsername.Trim(),
            password = settings.ParochosPassword
        };

        using var resp = await _http.PostAsJsonAsync(url, payload, JsonOpts);

        var body = await resp.Content.ReadAsStringAsync();

        if (!resp.IsSuccessStatusCode)
        {
            throw new InvalidOperationException(
                $"Parochos login απέτυχε (HTTP {(int)resp.StatusCode}). {body}");
        }

        using var doc = JsonDocument.Parse(body);
        var root = doc.RootElement;

        _jwt =
            root.GetProperty("jwt").GetString();

        _jwtExpirationUtc =
            root.GetProperty("jwtExpiration")
                .GetDateTime()
                .ToUniversalTime();

        _refreshToken =
            root.GetProperty("jwtRefreshToken").GetString();

        _refreshExpirationUtc =
            root.GetProperty("jwtRefreshTokenExpiration")
                .GetDateTime()
                .ToUniversalTime();

        // ΠΟΛΥ ΣΗΜΑΝΤΙΚΟ:
        // Το Parochos επιστρέφει το base URL στο login response.
        _baseUrl =
            root.GetProperty("url1")
                .GetString()
                ?.TrimEnd('/');

        _logger.LogInformation(
            "PAROCHOS LOGIN OK - JWT expiration: {Expiration}, BaseUrl: {BaseUrl}",
            _jwtExpirationUtc,
            _baseUrl);

        if (string.IsNullOrWhiteSpace(_jwt) ||
            string.IsNullOrWhiteSpace(_baseUrl))
        {
            throw new InvalidOperationException(
                "Ο Parochos επέστρεψε επιτυχή σύνδεση χωρίς jwt ή url1.");
        }
    }

    private async Task RefreshAsync(CompanySettings settings)
    {
        var url = $"{AccountBaseUrl(settings.ParochosUseProduction)}/api/token/Refresh";
        var payload = new { token = _jwt, refreshToken = _refreshToken };

        using var resp = await _http.PostAsJsonAsync(url, payload, JsonOpts);
        var body = await resp.Content.ReadAsStringAsync();
        if (!resp.IsSuccessStatusCode)
            throw new InvalidOperationException($"Parochos refresh απέτυχε (HTTP {(int)resp.StatusCode}). {body}");

        using var doc = JsonDocument.Parse(body);
        var root = doc.RootElement;
        _jwt = root.GetProperty("jwt").GetString();
        _jwtExpirationUtc = root.GetProperty("jwtExpiration").GetDateTime().ToUniversalTime();
        _refreshToken = root.GetProperty("jwtRefreshToken").GetString();
        _refreshExpirationUtc = root.GetProperty("jwtRefreshTokenExpiration").GetDateTime().ToUniversalTime();
    }

    public async Task<ParochosSendResult> SendAndWaitAsync(
        CompanySettings settings,
        object source,
        string externalSystemId,
        int? transmissionType = null)
    {
        try
        {
            await EnsureAuthenticatedAsync(settings);

            var payload = new
            {
                externalSystemId,
                identifier = "Parochos",
                transmissionType = transmissionType ?? settings.ParochosTransmissionType,
                source
            };

            // DEBUG: log the exact invoice header that is sent to Parochos.
            // This lets us verify the actual series/AA in the final JSON payload.
            try
            {
                if (source is Dictionary<string, object?> sourceDict &&
                    sourceDict.TryGetValue("invoice", out var invoiceObj) &&
                    invoiceObj is Dictionary<string, object?> invoiceDict &&
                    invoiceDict.TryGetValue("invoiceHeader", out var headerObj) &&
                    headerObj is Dictionary<string, object?> headerDict)
                {
                    headerDict.TryGetValue("series", out var sentSeries);
                    headerDict.TryGetValue("aa", out var sentAa);

                    _logger.LogWarning(
                        "PAROCHOS SEND DEBUG >>> ExternalSystemId={ExternalSystemId} | Series={Series} | AA={AA}",
                        externalSystemId,
                        sentSeries,
                        sentAa);
                }
            }
            catch (Exception debugEx)
            {
                _logger.LogWarning(debugEx, "PAROCHOS SEND DEBUG failed");
            }

            var result = await PostSignedAsync(settings, "/api/send", payload, ParseSendResponse);
            if (!result.InProgress)
                return result;

            for (var i = 0; i < settings.ParochosPollingAttempts; i++)
            {
                await Task.Delay(settings.ParochosPollingDelayMs);
                result = await PostSignedAsync(
                    settings,
                    "/api/get",
                    new { externalSystemId },
                    ParseSendResponse);

                if (!result.InProgress)
                    return result;
            }

            return new ParochosSendResult
            {
                Success = false,
                InProgress = true,
                ErrorMessage = "Ο Parochos δεν ολοκλήρωσε την επεξεργασία μέσα στο επιτρεπόμενο χρονικό διάστημα."
            };
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Σφάλμα αποστολής παραστατικού στον Parochos");
            return new ParochosSendResult { Success = false, ErrorMessage = ex.Message };
        }
    }

    /// <summary>
    /// Checks whether Parochos already has the document for this external ID.
    /// Used to recover a card receipt after a duplicate invoice-number response
    /// without submitting the signed document or charging the POS again.
    /// </summary>
    public async Task<ParochosSendResult> GetDocumentByExternalSystemIdAsync(
        CompanySettings settings,
        string externalSystemId)
    {
        try
        {
            await EnsureAuthenticatedAsync(settings);
            var result = await PostSignedAsync(
                settings,
                "/api/get",
                new { externalSystemId },
                ParseSendResponse);
            if (!result.InProgress)
                return result;

            for (var i = 0; i < settings.ParochosPollingAttempts; i++)
            {
                await Task.Delay(settings.ParochosPollingDelayMs);
                result = await PostSignedAsync(
                    settings,
                    "/api/get",
                    new { externalSystemId },
                    ParseSendResponse);
                if (!result.InProgress)
                    return result;
            }

            return new ParochosSendResult
            {
                InProgress = true,
                ProcessId = result.ProcessId,
                ErrorMessage = "Το παραστατικό παραμένει υπό επεξεργασία στον Parochos."
            };
        }
        catch (Exception ex)
        {
            _logger.LogError(ex,
                "Αποτυχία ανάκτησης παραστατικού Parochos για ExternalSystemId {ExternalSystemId}",
                externalSystemId);
            return new ParochosSendResult
            {
                Success = false,
                ErrorMessage = "Δεν ήταν δυνατός ο έλεγχος κατάστασης του παραστατικού στον Parochos."
            };
        }
    }

    /// <summary>
    /// Creates the provider payment signature. For EFT-POS (type 7) this must happen before Send.
    /// The returned signature then has to be committed to the POS terminal by the POS integration.
    /// </summary>
    public async Task<ParochosPaymentResult> RequestPaymentAsync(
        CompanySettings settings,
        string externalSystemId,
        string issuerVatNumber,
        DateTimeOffset invoiceIssueDate,
        int companyBranch,
        string invoiceType,
        string invoiceSeries,
        long invoiceNumber,
        decimal netValue,
        decimal vatAmount,
        decimal totalValue,
        decimal paymentAmount,
        string terminalId,
        string nspCode)
    {
        try
        {
            await EnsureAuthenticatedAsync(settings);

            var payload = new
            {
                externalSystemId,
                issuerVatNumber,
                invoiceIssueDate = invoiceIssueDate.ToString("yyyy-MM-dd'T'00:00:00"),
                companyBranch = companyBranch.ToString(),
                invoiceType,
                invoiceSeries,
                invoiceAA = invoiceNumber.ToString(),
                netValue,
                vatAmount,
                totalValue,
                paymentAmount,
                terminalId,
                specialInvoiceCategory = settings.ParochosSpecialInvoiceCategory,
                nspCode
            };

            return await PostSignedAsync(
                settings,
                "/api/requestPayment",
                payload,
                ParsePaymentResponse);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Σφάλμα requestPayment στον Parochos");
            return new ParochosPaymentResult { Success = false, ErrorMessage = ex.Message };
        }
    }

    public async Task<ParochosSimpleResult> SetPointOfServiceStateAsync(
        CompanySettings settings,
        string pointOfService,
        int actionKind,
        string? targetPointOfService = null)
    {
        try
        {
            await EnsureAuthenticatedAsync(settings);
            var payload = new
            {
                companyBranchCode = settings.Branch.ToString(),
                pointOfService,
                targetPointOfService = targetPointOfService ?? "",
                actionKind
            };
            return await PostSignedAsync(settings, "/api/pointOfService/SetState", payload, ParseSimpleResult);
        }
        catch (Exception ex)
        {
            return new ParochosSimpleResult { Success = false, ErrorMessage = ex.Message };
        }
    }

    public Task<ParochosSimpleResult> OpenPointOfServiceAsync(CompanySettings settings, string pointOfService) =>
        SetPointOfServiceStateAsync(settings, pointOfService, 0);

    public Task<ParochosSimpleResult> ClosePointOfServiceAsync(CompanySettings settings, string pointOfService) =>
        SetPointOfServiceStateAsync(settings, pointOfService, 1);

    /// <summary>
    /// Reads the current Point of Service details from Parochos.
    /// The official v3 API returns the current balance plus the documents
    /// contributing to that Point of Service.
    /// </summary>
    public async Task<ParochosPointOfServiceResult> ListPointOfServiceDetailsAsync(
        CompanySettings settings,
        string pointOfService,
        int filter = 0)
    {
        try
        {
            await EnsureAuthenticatedAsync(settings);

            var payload = new
            {
                companyBranchCode = settings.Branch.ToString(),
                refNumber = 0,
                pointOfService,
                filter
            };

            return await PostSignedAsync(
                settings,
                "/api/pointOfService/ListDetails",
                payload,
                ParsePointOfServiceDetailsResult);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex,
                "Σφάλμα ListDetails για PointOfService {PointOfService}",
                pointOfService);

            return new ParochosPointOfServiceResult
            {
                Success = false,
                ErrorMessage = ex.Message
            };
        }
    }

    public async Task<ParochosSimpleResult> AssignDocumentAsync(
        CompanySettings settings,
        string pointOfService,
        string processId,
        string externalSystemId)
    {
        try
        {
            await EnsureAuthenticatedAsync(settings);
            var payload = new
            {
                companyBranchCode = settings.Branch.ToString(),
                pointOfService,
                processId,
                externalSystemId
            };
            return await PostSignedAsync(settings, "/api/pointOfService/AssignDocument", payload, ParseSimpleResult);
        }
        catch (Exception ex)
        {
            return new ParochosSimpleResult { Success = false, ErrorMessage = ex.Message };
        }
    }

    public async Task<ParochosSimpleResult> ConnectDocumentsAsync(
        CompanySettings settings,
        string? orderFormProcessId,
        string? orderFormExternalSystemId,
        IEnumerable<long> documentMarks)
    {
        try
        {
            await EnsureAuthenticatedAsync(settings);
            var marks = documentMarks.Where(x => x > 0).Select(x => x.ToString()).ToList();
            if (string.IsNullOrWhiteSpace(orderFormProcessId) && string.IsNullOrWhiteSpace(orderFormExternalSystemId))
                return new ParochosSimpleResult { Success = false, ErrorMessage = "Λείπει processId ή externalSystemId του Order Form." };
            if (marks.Count == 0)
                return new ParochosSimpleResult { Success = false, ErrorMessage = "Δεν υπάρχει MARK τελικού παραστατικού για σύνδεση." };

            var payload = new
            {
                processId = orderFormProcessId,
                externalSystemId = orderFormExternalSystemId,
                documentMarks = marks
            };
            return await PostSignedAsync(settings, "/api/OrderForms/ConnectDocuments", payload, ParseConnectResult);
        }
        catch (Exception ex)
        {
            return new ParochosSimpleResult { Success = false, ErrorMessage = ex.Message };
        }
    }

    private async Task<T> PostSignedAsync<T>(
        CompanySettings settings,
        string path,
        object payload,
        Func<string, T> parser)
    {
        await EnsureAuthenticatedAsync(settings);
        var (status, body) = await PostOnceAsync(path, payload);

        // 401 = JWT invalid/expired: ξανά αυθεντικοποίηση και μία επανάληψη.
        if (status == 401)
        {
            _jwtExpirationUtc = DateTime.MinValue;
            await EnsureAuthenticatedAsync(settings);
            (status, body) = await PostOnceAsync(path, payload);
        }

        if (status < 200 || status > 299)
        {
            return parser(JsonSerializer.Serialize(new
            {
                status = 2,
                success = false,
                errorCategory = "HTTP",
                errorCode = status.ToString(),
                errorMessage = body
            }));
        }

        return parser(body);
    }

    private async Task<(int Status, string Body)> PostOnceAsync(string path, object payload)
    {
        var url = $"{_baseUrl}{path}";
        using var request = new HttpRequestMessage(HttpMethod.Post, url)
        {
            Content = JsonContent.Create(payload, options: JsonOpts)
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _jwt);
        request.Headers.Add("X-Version", "3.0");

        using var resp = await _http.SendAsync(request);
        var body = await resp.Content.ReadAsStringAsync();

        _logger.LogInformation(
            "PAROCHOS RESPONSE - URL: {Url} - HTTP: {StatusCode} - BODY: {Body}",
            url, (int)resp.StatusCode, body);

        return ((int)resp.StatusCode, body);
    }

    private static ParochosSendResult ParseSendResponse(string json)
    {
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        var status = GetInt(root, "status") ?? 0;
        var signing = root.TryGetProperty("signing", out var s) && s.ValueKind == JsonValueKind.Object ? s : default;

        long? mark = GetLong(signing, "mark");
        return new ParochosSendResult
        {
            Success = status == 1,
            InProgress = status == 0,
            ProcessId = GetString(root, "processId"),
            Mark = mark,
            Uid = GetString(signing, "uid"),
            AuthenticationCode = GetString(signing, "authenticationCode"),
            QrCode = GetString(signing, "qrCode") ?? GetString(signing, "qrUrlAADE"),
            PdfFileUrl = GetString(signing, "pdfFileUrl"),
            ErrorCategory = GetString(root, "errorCategory"),
            ErrorCode = GetString(root, "errorCode"),
            ErrorMessage = status == 2 ? GetString(root, "errorMessage") ?? "Το παραστατικό απορρίφθηκε από τον Parochos/ΑΑΔΕ." : null
        };
    }

    private static ParochosPaymentResult ParsePaymentResponse(string json)
    {
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        var status = GetInt(root, "status") ?? 0;
        var token = root.TryGetProperty("paymentToken", out var p) && p.ValueKind == JsonValueKind.Object ? p : default;
        return new ParochosPaymentResult
        {
            Success = status == 1,
            ExternalSystemId = GetString(root, "externalSystemId"),
            ProcessId = GetString(root, "processId"),
            Uid = GetString(root, "uid"),
            Signature = GetString(token, "signature"),
            SignatureClearText = GetString(token, "signatureClearText"),
            Amount = GetDecimal(token, "amount") ?? 0m,
            Timestamp = GetDateTimeOffset(token, "timestamp"),
            ErrorCategory = GetString(root, "errorCategory"),
            ErrorCode = GetString(root, "errorCode"),
            ErrorMessage = status == 2 ? GetString(root, "errorMessage") ?? "Το requestPayment απέτυχε." : null
        };
    }

    private static ParochosSimpleResult ParseSimpleResult(string json)
    {
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        return new ParochosSimpleResult
        {
            Success = GetBool(root, "success") ?? ((GetInt(root, "status") ?? 0) == 1),
            ErrorCategory = GetString(root, "errorCategory"),
            ErrorCode = GetString(root, "errorCode"),
            ErrorMessage = GetString(root, "errorMessage")
        };
    }

    private static ParochosSimpleResult ParseConnectResult(string json)
    {
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        var status = GetInt(root, "status");
        var connectedCount = GetInt(root, "connectedCount");
        return new ParochosSimpleResult
        {
            // Parochos defines status=1 as Completed. A zero connectedCount
            // does not by itself mean that the request failed: the accounting
            // document may already have been correlated during /api/send via
            // invoice.correlatedInvoices, so ConnectDocuments has nothing new
            // to connect. Keep the count for diagnostics, but use the documented
            // completion status to decide whether the API operation failed.
            Success = status == 1,
            ConnectedCount = connectedCount,
            ErrorCategory = GetString(root, "errorCategory"),
            ErrorCode = GetString(root, "errorCode"),
            ErrorMessage = GetString(root, "errorMessage")
        };
    }

    private static ParochosPointOfServiceResult ParsePointOfServiceDetailsResult(string json)
    {
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        var result = new ParochosPointOfServiceResult
        {
            Success = GetBool(root, "success") ?? false,
            ErrorCategory = GetString(root, "errorCategory"),
            ErrorCode = GetString(root, "errorCode"),
            ErrorMessage = GetString(root, "errorMessage")
        };

        if (!result.Success)
            return result;

        if (!root.TryGetProperty("data", out var data) ||
            data.ValueKind != JsonValueKind.Array)
            return result;

        var first = data.EnumerateArray().FirstOrDefault();
        if (first.ValueKind == JsonValueKind.Object)
        {
            result = new ParochosPointOfServiceResult
            {
                Success = true,
                PointOfService = GetString(first, "pointOfService"),
                Balance = GetDecimal(first, "balance"),
                RefNumber = GetInt(first, "refNumber"),
                ErrorCategory = GetString(root, "errorCategory"),
                ErrorCode = GetString(root, "errorCode"),
                ErrorMessage = GetString(root, "errorMessage")
            };

            if (first.TryGetProperty("details", out var details) &&
                details.ValueKind == JsonValueKind.Array)
            {
                foreach (var detail in details.EnumerateArray())
                {
                    result.Details.Add(new ParochosPointOfServiceDetail
                    {
                        ExternalSystemId = GetString(detail, "externalSystemId"),
                        Mark = GetLong(detail, "mark"),
                        InvoiceType = GetString(detail, "invoiceType"),
                        TotalAmount = GetDecimal(detail, "totalAmount"),
                        TransactionAmount = GetDecimal(detail, "transactionAmount"),
                        InvoiceCode = GetString(detail, "invoiceCode"),
                        OrderFormStatus = GetInt(detail, "orderFormStatus")
                    });
                }
            }
        }

        return result;
    }

    private static string? GetString(JsonElement element, string name) =>
        element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out var p) && p.ValueKind != JsonValueKind.Null ? p.ToString() : null;

    private static int? GetInt(JsonElement element, string name) =>
        element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out var p) && p.ValueKind == JsonValueKind.Number && p.TryGetInt32(out var v) ? v : null;

    private static long? GetLong(JsonElement element, string name) =>
        element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out var p) && p.ValueKind == JsonValueKind.Number && p.TryGetInt64(out var v) ? v : null;

    private static decimal? GetDecimal(JsonElement element, string name) =>
        element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out var p) && p.ValueKind == JsonValueKind.Number && p.TryGetDecimal(out var v) ? v : null;

    private static bool? GetBool(JsonElement element, string name)
    {
        if (element.ValueKind != JsonValueKind.Object || !element.TryGetProperty(name, out var p))
            return null;
        return p.ValueKind is JsonValueKind.True or JsonValueKind.False ? p.GetBoolean() : null;
    }

    private static DateTimeOffset? GetDateTimeOffset(JsonElement element, string name)
    {
        var s = GetString(element, name);
        return DateTimeOffset.TryParse(s, out var value) ? value : null;
    }
}
