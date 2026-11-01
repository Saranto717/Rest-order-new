using RestaurantOrders.Models;

namespace RestaurantOrders.Services;

public static class MellonConnectionSettings
{
    public const string DemoBaseUrl = CommonWebEcrProviderProfile.DemoHost + CommonWebEcrProviderProfile.ApiPath + "/";
    public const string DemoAuthUrl = CommonWebEcrProviderProfile.DemoHost + "/api/token/";
    public const string DemoRedeemUrl = DemoBaseUrl + "authorization/redeem/";

    public static bool TryResolve(BankPosTerminalSettings settings, out BankPosTerminalSettings resolved, out string error)
    {
        resolved = settings;
        error = "";
        if (!CommonWebEcrProviderProfile.IsCommonRest(settings))
        {
            error = "Το επιλεγμένο POS δεν χρησιμοποιεί το πρωτόκολλο Mellon WebECR REST.";
            return false;
        }
        var baseUrl = settings.UseProduction ? settings.Endpoint.Trim() : DemoBaseUrl;
        var authUrl = settings.UseProduction ? settings.AuthEndpoint.Trim() : DemoAuthUrl;
        var redeemUrl = settings.UseProduction ? settings.RedeemEndpoint.Trim() : DemoRedeemUrl;
        foreach (var value in new[] { baseUrl, authUrl, redeemUrl })
        {
            if (!Uri.TryCreate(value, UriKind.Absolute, out var uri) ||
                (uri.Scheme != Uri.UriSchemeHttps && uri.Scheme != Uri.UriSchemeHttp) ||
                !string.IsNullOrEmpty(uri.Query) || !string.IsNullOrEmpty(uri.Fragment))
            {
                error = "Συμπλήρωσε έγκυρα πλήρη Base, Auth και Redeem URLs παραγωγής για το Mellon POS.";
                return false;
            }
            if (settings.UseProduction && (uri.Host.Equals("uat.mreceipts.com", StringComparison.OrdinalIgnoreCase) ||
                uri.Host.Equals("webecr-test.epayworldwide.com", StringComparison.OrdinalIgnoreCase)))
            {
                error = "Το παραγωγικό Mellon POS δεν μπορεί να χρησιμοποιεί demo URLs.";
                return false;
            }
        }
        resolved = new BankPosTerminalSettings
        {
            Id = settings.Id, Name = settings.Name, Provider = settings.Provider, Protocol = settings.Protocol,
            UseProduction = settings.UseProduction, IsEnabled = settings.IsEnabled,
            TerminalId = settings.TerminalId, IdUrl = settings.IdUrl, ProviderCode = settings.ProviderCode,
            ParochosNspCode = settings.ParochosNspCode, ProviderId = settings.ProviderId,
            MerchantId = settings.MerchantId, ApiKey = settings.ApiKey, ApiSecret = settings.ApiSecret,
            Username = settings.Username, Password = settings.Password, RedeemCode = settings.RedeemCode,
            Endpoint = baseUrl, AuthEndpoint = authUrl, RedeemEndpoint = redeemUrl,
            TimeoutSeconds = settings.TimeoutSeconds
        };
        return true;
    }

    // Mellon rows are connected after Redeem and assignment to a physical terminal.
    // Network availability is confirmed when the transaction is sent.
    public static bool IsConnected(BankPosTerminalSettings settings) =>
        settings.IsEnabled && !string.IsNullOrWhiteSpace(settings.TerminalId) &&
        !string.IsNullOrWhiteSpace(settings.ApiKey) && !string.IsNullOrWhiteSpace(settings.Username) &&
        !string.IsNullOrWhiteSpace(settings.Password) && TryResolve(settings, out _, out _);
}
