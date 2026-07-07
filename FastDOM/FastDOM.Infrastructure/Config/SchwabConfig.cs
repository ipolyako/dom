namespace FastDOM.Infrastructure.Config;

/// <summary>
/// Non-secret Schwab configuration. Client ID and secret are stored in Windows Credential Manager,
/// never in this file.
/// </summary>
public class SchwabConfig
{
    public string AppKey { get; set; } = string.Empty;
    public string CallbackUrl { get; set; } = "https://127.0.0.1:8182";
    // NOTE: Schwab does NOT offer a confirmed public sandbox/paper trading API endpoint.
    // The schwab-py library explicitly states "paper trading is not supported."
    // "Sandbox" mode in FastDOM means the app is connected but no live orders are placed
    // (all orders are validated and logged but blocked by the risk manager).
    // If you have a Schwab paper account, you can connect to the live API with that account.
    public bool UseSandbox { get; set; } = true;

    // Official Schwab API base URLs (confirmed from developer.schwab.com)
    public string AuthorizeUrl { get; } = "https://api.schwabapi.com/v1/oauth/authorize";
    public string TokenUrl { get; } = "https://api.schwabapi.com/v1/oauth/token";
    public string TraderApiBase { get; } = "https://api.schwabapi.com/trader/v1";
    public string MarketDataApiBase { get; } = "https://api.schwabapi.com/marketdata/v1";

    public int AccessTokenExpirySeconds { get; } = 1800;   // 30 minutes
    public int RefreshTokenExpiryDays { get; } = 7;

    // Refresh 5 minutes before expiry
    public int PreemptiveRefreshSeconds { get; set; } = 300;
}
