namespace RestaurantOrders.Models;

/// <summary>Ρυθμίσεις σύνδεσης EFT-POS ανά πάροχο/τερματικό. Δεν εκτελεί από μόνη της χρέωση.</summary>
public class BankPosTerminalSettings
{
    public int Id { get; set; }
    public string Name { get; set; } = "Κύριο POS";
    public string Provider { get; set; } = "Viva";
    public string Protocol { get; set; } = "Viva Cloud";
    public bool UseProduction { get; set; }
    public bool IsEnabled { get; set; }
    public string TerminalId { get; set; } = "";
    /// <summary>Provider-specific identifier URL/ID used by some Mellon-compatible flows.</summary>
    public string IdUrl { get; set; } = "";
    /// <summary>One-time code supplied by the terminal for a provider-specific Redeem flow.</summary>
    public string RedeemCode { get; set; } = "";
    public string ProviderCode { get; set; } = "";
    /// <summary>Optional Parochos NSP code override for this terminal.</summary>
    public string ParochosNspCode { get; set; } = "";
    /// <summary>myDATA provider ID embedded in signed ProviderData; Parochos is currently 19.</summary>
    public int ProviderId { get; set; } = 19;
    public bool IsAppToApp { get; set; }
    public string MerchantId { get; set; } = "";
    public string ApiKey { get; set; } = "";
    public string ApiSecret { get; set; } = "";
    public string Username { get; set; } = "";
    public string Password { get; set; } = "";
    public string Endpoint { get; set; } = "https://webecr-test.epayworldwide.com/";
    /// <summary>Full token URL; blank uses Base URL + token/.</summary>
    public string AuthEndpoint { get; set; } = "";
    /// <summary>Full redeem URL; blank uses Base URL + authorization/redeem/.</summary>
    public string RedeemEndpoint { get; set; } = "";
    public string IpAddress { get; set; } = "";
    public int Port { get; set; }
    public int TimeoutSeconds { get; set; } = 60;
    public string Notes { get; set; } = "";
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
}
