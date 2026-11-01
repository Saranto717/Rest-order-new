using RestaurantOrders.Models;

namespace RestaurantOrders.Services;

/// <summary>
/// Profiles for banks using the shared Mellon EFTPOS-WebECR REST protocol.
/// Production endpoints belong to each saved terminal; demo endpoints are shared.
/// </summary>
public static class CommonWebEcrProviderProfile
{
    public const string ProtocolName = "Common WebECR (Mellon API v2.5.7)";

    private static readonly string[] SupportedProviders =
    {
        "NBG", "NBG / NSP προς επιβεβαίωση", "Wordline / Cardlink", "Wordline", "Cardlink",
        "NEXI", "Nexi", "myPOS", "Worldline", "Mellon", "JCC", "Attica", "Attica Bank", "Pancreta",
        "Pancreta Bank", "TORA", "PBT", "GBL", "Worldline / Cardlink"
    };

    public static bool IsSupportedProvider(string? provider) =>
        !string.IsNullOrWhiteSpace(provider) &&
        SupportedProviders.Contains(provider.Trim(), StringComparer.OrdinalIgnoreCase);

    public static bool IsCommonRest(BankPosTerminalSettings? settings) =>
        settings is not null &&
        IsSupportedProvider(settings.Provider) &&
        settings.Protocol.Contains("Common WebECR", StringComparison.OrdinalIgnoreCase) &&
        !settings.Protocol.Contains("TCP", StringComparison.OrdinalIgnoreCase);

    public static bool SupportsPayment(BankPosTerminalSettings? settings)
    {
        if (settings is null) return false;
        var isEpay = settings.Provider.Contains("ePay", StringComparison.OrdinalIgnoreCase) ||
                     settings.Provider.Contains("Euronet", StringComparison.OrdinalIgnoreCase);
        return isEpay
            ? settings.Protocol.Contains("WebECR", StringComparison.OrdinalIgnoreCase) &&
              !settings.Protocol.Contains("TCP", StringComparison.OrdinalIgnoreCase)
            : IsCommonRest(settings);
    }

    /// <summary>
    /// Legacy grouping retained for compatibility with the existing ePay client API.
    /// Mellon checkout does not use bank overrides or inferred production URLs.
    /// </summary>
    public static string? BankGroupFor(string? provider) => provider?.Trim().ToUpperInvariant() switch
    {
        "WORDLINE / CARDLINK" or "WORDLINE" or "WORLDLINE" or "CARDLINK" => "worldline",
        "NBG" or "NBG / NSP ΠΡΟΣ ΕΠΙΒΕΒΑΙΩΣΗ" or "MYPOS" => "nbg",
        "NEXI" => "nexi",
        "JCC" or "ATTICA" or "ATTICA BANK" or "PANCRETA" or "PANCRETA BANK" or "TORA" or "PBT" => "gbl",
        _ => null
    };

    /// <summary>Demo/UAT host shared by all Mellon banks.</summary>
    public const string DemoHost = "https://uat.mreceipts.com";

    /// <summary>Mellon API version path (verified: /api/v2.2/terminal/, /api/v2.2/authorization/redeem/).</summary>
    public const string ApiPath = "/api/v2.2";

    /// <summary>Production URLs are entered by the operator; demo is common to all banks.</summary>
    public static string? HostFor(string? provider, bool useProduction = true) =>
        IsSupportedProvider(provider) && !useProduction ? DemoHost : null;

    /// <summary>API base URL: host + /api/v2.2 (terminal/, txninit/, transactionintent/ are relative to it).</summary>
    public static string? BaseUrlFor(string? provider, bool useProduction = true) =>
        HostFor(provider, useProduction) is { } h ? h + ApiPath : null;

    /// <summary>Auth URL: host + /api/token/ (refresh = /api/token/refresh/).</summary>
    public static string? AuthUrlFor(string? provider, bool useProduction = true) =>
        HostFor(provider, useProduction) is { } h ? h + "/api/token/" : null;

    /// <summary>Redeem URL: host + /api/v2.2/authorization/redeem/.</summary>
    public static string? RedeemUrlFor(string? provider, bool useProduction = true) =>
        BaseUrlFor(provider, useProduction) is { } b ? b + "/authorization/redeem/" : null;

    /// <summary>Known Parochos appendix codes. Null means the operator must enter the provider-confirmed code.</summary>
    public static string? ParochosNspCodeFor(string? provider) => provider?.Trim() switch
    {
        "Wordline / Cardlink" or "Worldline / Cardlink" or "Wordline" or "Worldline" or "Cardlink" => "201",
        "Mellon" => "3",
        "NEXI" => "4",
        "Attica" or "Attica Bank" => "210",
        "Pancreta" or "Pancreta Bank" => "211",
        "JCC" => "212",
        "PBT" => "213",
        "myPOS" => "214",
        _ => null
    };

    private static readonly string[] KnownHosts =
    {
        "https://wl.mreceipts.com", "https://www.mreceipts.com", "https://nexi.mreceipts.com",
        "https://gbl.mreceipts.com", "https://uat.mreceipts.com"
    };

    public static bool IsKnownCommonBaseUrl(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return false;
        var normalized = value.Trim().TrimEnd('/');
        return KnownHosts.Any(h => normalized.Equals(h, StringComparison.OrdinalIgnoreCase) ||
                                   normalized.StartsWith(h + "/api", StringComparison.OrdinalIgnoreCase)) ||
               normalized.StartsWith("https://webecr-test.epayworldwide.com", StringComparison.OrdinalIgnoreCase);
    }

    public static string ProviderLabel(string? provider) =>
        string.IsNullOrWhiteSpace(provider) ? "WebECR" : provider.Trim();
}
