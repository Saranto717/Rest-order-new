using System.Net.Http.Headers;
using System.Text;
using System.Xml.Linq;

namespace RestaurantOrders.Services;

public sealed class AadeCustomerActivity
{
    public string Code { get; init; } = "";
    public string Description { get; init; } = "";
    public string Kind { get; init; } = "";
}

public sealed class AadeCustomerInfo
{
    public string VatNumber { get; init; } = "";
    public string Name { get; init; } = "";
    public string CommercialTitle { get; init; } = "";
    public string LegalStatus { get; init; } = "";
    public string DoyCode { get; init; } = "";
    public string DoyDescription { get; init; } = "";
    public string Street { get; init; } = "";
    public string StreetNumber { get; init; } = "";
    public string PostalCode { get; init; } = "";
    public string Area { get; init; } = "";
    public string DeactivationStatus { get; init; } = "";
    public string FirmStatus { get; init; } = "";
    public string NormalVatSystem { get; init; } = "";
    public string MainActivity { get; init; } = "";
    public List<AadeCustomerActivity> Activities { get; init; } = new();

    public string Address =>
        string.Join(" ", new[] { Street?.Trim(), StreetNumber?.Trim() }
            .Where(x => !string.IsNullOrWhiteSpace(x)));

    public string City => Area?.Trim() ?? "";
}

public sealed class AadeRegistryLookupResult
{
    public bool Success { get; init; }
    public AadeCustomerInfo? Customer { get; init; }
    public string? ErrorCode { get; init; }
    public string? ErrorMessage { get; init; }
}

/// <summary>
/// Client for AADE's "Basic Registry Details of Businesses" SOAP service (RgWsPublic2).
/// Credentials are read from configuration; never hard-code them in source code.
/// </summary>
public sealed class AadeRegistryService
{
    private const string Endpoint = "https://www1.gsis.gr/wsaade/RgWsPublic2/RgWsPublic2";
    private const string WsseNamespace = "http://docs.oasis-open.org/wss/2004/01/oasis-200401-wss-wssecurity-secext-1.0.xsd";
    private const string ServiceNamespace = "http://rgwspublic2/RgWsPublic2Service";
    private const string ModelNamespace = "http://rgwspublic2/RgWsPublic2";

    private readonly HttpClient _http;
    private readonly IConfiguration _configuration;
    private readonly ILogger<AadeRegistryService> _logger;

    public AadeRegistryService(
        IHttpClientFactory httpClientFactory,
        IConfiguration configuration,
        ILogger<AadeRegistryService> logger)
    {
        _http = httpClientFactory.CreateClient("AADE-Registry");
        _configuration = configuration;
        _logger = logger;

        _http.Timeout = TimeSpan.FromSeconds(20);
    }

    public async Task<AadeRegistryLookupResult> LookupAsync(
        string afmCalledFor,
        string? afmCalledBy = null,
        CancellationToken cancellationToken = default)
    {
        var calledFor = NormalizeVat(afmCalledFor);

        if (!IsValidVat(calledFor))
        {
            return new AadeRegistryLookupResult
            {
                Success = false,
                ErrorCode = "INVALID_AFM",
                ErrorMessage = "Το ΑΦΜ πρέπει να αποτελείται από 9 ψηφία."
            };
        }

        var username = FirstNonEmpty(
            _configuration["AadeRegistry:Username"],
            Environment.GetEnvironmentVariable("AADE_REGISTRY_USERNAME"));
        var password = FirstNonEmpty(
            _configuration["AadeRegistry:Password"],
            Environment.GetEnvironmentVariable("AADE_REGISTRY_PASSWORD"));

        if (string.IsNullOrWhiteSpace(username) || string.IsNullOrWhiteSpace(password))
        {
            return new AadeRegistryLookupResult
            {
                Success = false,
                ErrorCode = "MISSING_CREDENTIALS",
                ErrorMessage =
                    "Δεν έχουν ρυθμιστεί οι ειδικοί κωδικοί της υπηρεσίας Αναζήτησης Βασικών Στοιχείων Μητρώου της ΑΑΔΕ."
            };
        }

        var calledBy = NormalizeVat(FirstNonEmpty(
            afmCalledBy,
            _configuration["AadeRegistry:CalledByVat"],
            Environment.GetEnvironmentVariable("AADE_REGISTRY_CALLED_BY")) ?? "");

        // afm_called_by is allowed to be empty by the AADE service.
        var requestXml = BuildSoapRequest(username, password, calledBy, calledFor, DateOnly.FromDateTime(DateTime.Today));

        try
        {
            using var content = new StringContent(requestXml, Encoding.UTF8, "application/soap+xml");
            content.Headers.ContentType = MediaTypeHeaderValue.Parse(
                "application/soap+xml; charset=utf-8; action=\"http://rgwspublic2/RgWsPublic2Service/rgWsPublic2AfmMethod\"");

            using var response = await _http.PostAsync(Endpoint, content, cancellationToken);
            var responseXml = await response.Content.ReadAsStringAsync(cancellationToken);

            if (!response.IsSuccessStatusCode)
            {
                _logger.LogWarning(
                    "AADE Registry HTTP error: {StatusCode}",
                    (int)response.StatusCode);

                return new AadeRegistryLookupResult
                {
                    Success = false,
                    ErrorCode = $"HTTP_{(int)response.StatusCode}",
                    ErrorMessage = "Η υπηρεσία μητρώου της ΑΑΔΕ δεν απάντησε επιτυχώς."
                };
            }

            return ParseResponse(responseXml, calledFor);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return new AadeRegistryLookupResult
            {
                Success = false,
                ErrorCode = "TIMEOUT",
                ErrorMessage = "Η αναζήτηση ΑΦΜ στην ΑΑΔΕ έληξε λόγω timeout."
            };
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Σφάλμα αναζήτησης ΑΦΜ στην υπηρεσία μητρώου της ΑΑΔΕ.");
            return new AadeRegistryLookupResult
            {
                Success = false,
                ErrorCode = "EXCEPTION",
                ErrorMessage = "Δεν ήταν δυνατή η επικοινωνία με την υπηρεσία μητρώου της ΑΑΔΕ."
            };
        }
    }

    private static string BuildSoapRequest(
        string username,
        string password,
        string afmCalledBy,
        string afmCalledFor,
        DateOnly asOnDate)
    {
        static string Escape(string value) =>
            System.Security.SecurityElement.Escape(value) ?? "";

        var calledByXml = string.IsNullOrWhiteSpace(afmCalledBy)
            ? "<ns3:afm_called_by/>"
            : $"<ns3:afm_called_by>{Escape(afmCalledBy)}</ns3:afm_called_by>";

        return $"""
            <env:Envelope xmlns:env="http://www.w3.org/2003/05/soap-envelope"
                          xmlns:ns1="{WsseNamespace}"
                          xmlns:ns2="{ServiceNamespace}"
                          xmlns:ns3="{ModelNamespace}">
              <env:Header>
                <ns1:Security>
                  <ns1:UsernameToken>
                    <ns1:Username>{Escape(username)}</ns1:Username>
                    <ns1:Password>{Escape(password)}</ns1:Password>
                  </ns1:UsernameToken>
                </ns1:Security>
              </env:Header>
              <env:Body>
                <ns2:rgWsPublic2AfmMethod>
                  <ns2:INPUT_REC>
                    {calledByXml}
                    <ns3:afm_called_for>{Escape(afmCalledFor)}</ns3:afm_called_for>
                    <ns3:as_on_date>{asOnDate:yyyy-MM-dd}</ns3:as_on_date>
                  </ns2:INPUT_REC>
                </ns2:rgWsPublic2AfmMethod>
              </env:Body>
            </env:Envelope>
            """;
    }

    private static AadeRegistryLookupResult ParseResponse(string xml, string requestedVat)
    {
        XDocument doc;

        try
        {
            doc = XDocument.Parse(xml, LoadOptions.PreserveWhitespace);
        }
        catch
        {
            return new AadeRegistryLookupResult
            {
                Success = false,
                ErrorCode = "INVALID_XML",
                ErrorMessage = "Η ΑΑΔΕ επέστρεψε μη έγκυρη απάντηση."
            };
        }

        var fault = doc.Descendants()
            .FirstOrDefault(e => string.Equals(e.Name.LocalName, "Fault", StringComparison.OrdinalIgnoreCase));
        if (fault is not null)
        {
            var reason = DescendantValue(fault, "Text")
                         ?? DescendantValue(fault, "faultstring")
                         ?? "Η υπηρεσία μητρώου της ΑΑΔΕ επέστρεψε SOAP Fault.";

            return new AadeRegistryLookupResult
            {
                Success = false,
                ErrorCode = "SOAP_FAULT",
                ErrorMessage = reason.Trim()
            };
        }

        var resultNode = doc.Descendants()
            .FirstOrDefault(e => string.Equals(e.Name.LocalName, "rg_ws_public2_result_rtType", StringComparison.OrdinalIgnoreCase));

        if (resultNode is null)
        {
            return new AadeRegistryLookupResult
            {
                Success = false,
                ErrorCode = "NO_RESULT",
                ErrorMessage = "Η ΑΑΔΕ δεν επέστρεψε στοιχεία για το συγκεκριμένο ΑΦΜ."
            };
        }

        var errorNode = resultNode.Elements()
            .FirstOrDefault(e => string.Equals(e.Name.LocalName, "error_rec", StringComparison.OrdinalIgnoreCase));

        var errorCode = ChildValue(errorNode, "error_code");
        var errorDescription = ChildValue(errorNode, "error_descr");

        if (!string.IsNullOrWhiteSpace(errorCode) || !string.IsNullOrWhiteSpace(errorDescription))
        {
            return new AadeRegistryLookupResult
            {
                Success = false,
                ErrorCode = errorCode?.Trim(),
                ErrorMessage = string.IsNullOrWhiteSpace(errorDescription)
                    ? "Η ΑΑΔΕ δεν επέστρεψε στοιχεία για το συγκεκριμένο ΑΦΜ."
                    : errorDescription.Trim()
            };
        }

        var basic = resultNode.Elements()
            .FirstOrDefault(e => string.Equals(e.Name.LocalName, "basic_rec", StringComparison.OrdinalIgnoreCase));

        if (basic is null)
        {
            return new AadeRegistryLookupResult
            {
                Success = false,
                ErrorCode = "NO_BASIC_RECORD",
                ErrorMessage = "Δεν βρέθηκαν βασικά στοιχεία μητρώου για το συγκεκριμένο ΑΦΜ."
            };
        }

        var activities = resultNode.Descendants()
            .Where(e => string.Equals(e.Name.LocalName, "item", StringComparison.OrdinalIgnoreCase)
                        && e.Parent is not null
                        && string.Equals(e.Parent.Name.LocalName, "firm_act_tab", StringComparison.OrdinalIgnoreCase))
            .Select(item => new AadeCustomerActivity
            {
                Code = ChildValue(item, "firm_act_code")?.Trim() ?? "",
                Description = ChildValue(item, "firm_act_descr")?.Trim() ?? "",
                Kind = ChildValue(item, "firm_act_kind_descr")?.Trim()
                       ?? ChildValue(item, "firm_act_kind")?.Trim()
                       ?? ""
            })
            .Where(a => !string.IsNullOrWhiteSpace(a.Code) || !string.IsNullOrWhiteSpace(a.Description))
            .ToList();

        var mainActivity = activities.FirstOrDefault(a =>
            string.Equals(a.Kind, "1", StringComparison.OrdinalIgnoreCase) ||
            a.Kind.Contains("ΚΥΡ", StringComparison.OrdinalIgnoreCase) ||
            a.Kind.Contains("MAIN", StringComparison.OrdinalIgnoreCase));

        var customer = new AadeCustomerInfo
        {
            VatNumber = (ChildValue(basic, "afm") ?? requestedVat).Trim(),
            DoyCode = ChildValue(basic, "doy")?.Trim() ?? "",
            DoyDescription = ChildValue(basic, "doy_descr")?.Trim() ?? "",
            DeactivationStatus = ChildValue(basic, "deactivation_flag_desc")?.Trim() ?? "",
            FirmStatus = ChildValue(basic, "firm_flag_descr")?.Trim() ?? "",
            Name = FirstNonEmpty(
                       ChildValue(basic, "onomasia")?.Trim(),
                       ChildValue(basic, "commer_title")?.Trim()) ?? "",
            CommercialTitle = ChildValue(basic, "commer_title")?.Trim() ?? "",
            LegalStatus = ChildValue(basic, "legal_status_descr")?.Trim() ?? "",
            Street = ChildValue(basic, "postal_address")?.Trim() ?? "",
            StreetNumber = ChildValue(basic, "postal_address_no")?.Trim() ?? "",
            PostalCode = ChildValue(basic, "postal_zip_code")?.Trim() ?? "",
            Area = ChildValue(basic, "postal_area_description")?.Trim() ?? "",
            NormalVatSystem = ChildValue(basic, "normal_vat_system_flag")?.Trim() ?? "",
            MainActivity = mainActivity?.Description ?? "",
            Activities = activities
        };

        return new AadeRegistryLookupResult
        {
            Success = true,
            Customer = customer
        };
    }

    private static string? DescendantValue(XElement? node, string localName) =>
        node?.Descendants().FirstOrDefault(e =>
            string.Equals(e.Name.LocalName, localName, StringComparison.OrdinalIgnoreCase))?.Value;

    private static string? ChildValue(XElement? node, string localName) =>
        node?.Elements().FirstOrDefault(e =>
            string.Equals(e.Name.LocalName, localName, StringComparison.OrdinalIgnoreCase))?.Value;

    private static string? FirstNonEmpty(params string?[] values) =>
        values.FirstOrDefault(v => !string.IsNullOrWhiteSpace(v));

    private static string NormalizeVat(string? value) =>
        new string((value ?? "").Where(char.IsDigit).ToArray());

    private static bool IsValidVat(string value)
    {
        if (value.Length != 9 || !value.All(char.IsDigit))
            return false;

        // Greek AFM checksum. Keep it local so a typo is caught before a network call.
        var sum = 0;
        for (var i = 0; i < 8; i++)
            sum += (value[i] - '0') * (1 << (8 - i));

        return sum % 11 % 10 == value[8] - '0';
    }
}
