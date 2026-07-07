using System.Diagnostics;
using System.Net;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json;
using System.Web;
using FastDOM.Broker.Interfaces;
using FastDOM.Infrastructure.Config;
using FastDOM.Infrastructure.Security;
using Microsoft.Extensions.Logging;

namespace FastDOM.Broker.Schwab.Auth;

/// <summary>
/// Implements OAuth 2.0 authorization_code flow for the Schwab Trader API.
///
/// Official endpoints (confirmed from developer.schwab.com):
///   Authorize: https://api.schwabapi.com/v1/oauth/authorize
///   Token:     https://api.schwabapi.com/v1/oauth/token
///
/// Access token lifetime:  30 minutes (1800 seconds)
/// Refresh token lifetime: 7 days (hard expiration — requires full re-auth after)
///
/// The callback MUST use https://127.0.0.1:{port} — not localhost — for desktop apps.
/// </summary>
public class SchwabAuthProvider : IAuthProvider
{
    private const string KeyAccessToken = "schwab_access_token";
    private const string KeyRefreshToken = "schwab_refresh_token";
    private const string KeyTokenExpiry = "schwab_token_expiry";
    private const string KeyRefreshExpiry = "schwab_refresh_expiry";
    private const string KeyAppSecret = "schwab_app_secret";

    private readonly ILogger<SchwabAuthProvider> _logger;
    private readonly SchwabConfig _config;
    private readonly SecureStorage _storage;
    private readonly HttpClient _http;

    private string? _accessToken;
    private DateTime _accessTokenExpiry = DateTime.MinValue;
    private DateTime _refreshTokenExpiry = DateTime.MinValue;

    public bool IsAuthenticated => !string.IsNullOrEmpty(_accessToken) &&
                                   DateTime.UtcNow < _accessTokenExpiry;

    public DateTime? TokenExpiresAt => _accessTokenExpiry == DateTime.MinValue ? null : _accessTokenExpiry;
    public DateTime? RefreshTokenExpiresAt => _refreshTokenExpiry == DateTime.MinValue ? null : _refreshTokenExpiry;

    public bool NeedsReauth =>
        string.IsNullOrEmpty(_storage.Retrieve(KeyRefreshToken)) ||
        DateTime.UtcNow >= _refreshTokenExpiry;

    public SchwabAuthProvider(
        ILogger<SchwabAuthProvider> logger,
        SchwabConfig config,
        SecureStorage storage)
    {
        _logger = logger;
        _config = config;
        _storage = storage;
        _http = new HttpClient();
        LoadStoredExpiries();
    }

    private void LoadStoredExpiries()
    {
        var expiryStr = _storage.Retrieve(KeyTokenExpiry);
        if (DateTime.TryParse(expiryStr, out var exp)) _accessTokenExpiry = exp;

        var refreshStr = _storage.Retrieve(KeyRefreshExpiry);
        if (DateTime.TryParse(refreshStr, out var rexp)) _refreshTokenExpiry = rexp;
    }

    /// <summary>
    /// Stores the App Secret securely. Call once during setup.
    /// The App Key (client_id) is stored in SchwabConfig (non-secret).
    /// </summary>
    public void StoreAppSecret(string secret) => _storage.Store(KeyAppSecret, secret);

    public async Task<bool> LoginAsync(CancellationToken ct = default)
    {
        if (string.IsNullOrEmpty(_config.AppKey))
        {
            _logger.LogError("Schwab App Key not configured");
            return false;
        }

        var secret = _storage.Retrieve(KeyAppSecret);
        if (string.IsNullOrEmpty(secret))
        {
            _logger.LogError("Schwab App Secret not found in secure storage. Run setup first.");
            return false;
        }

        // Extract port from callback URL
        if (!Uri.TryCreate(_config.CallbackUrl, UriKind.Absolute, out var callbackUri))
        {
            _logger.LogError("Invalid callback URL: {Url}", _config.CallbackUrl);
            return false;
        }
        int port = callbackUri.Port;

        // Build authorization URL
        var authUrl = $"{_config.AuthorizeUrl}?" +
            $"client_id={Uri.EscapeDataString(_config.AppKey)}" +
            $"&redirect_uri={Uri.EscapeDataString(_config.CallbackUrl)}" +
            $"&response_type=code" +
            $"&scope=api";

        _logger.LogInformation("Opening browser for Schwab OAuth authorization...");

        // Start local HTTPS listener to capture the callback
        // Note: requires a self-signed cert bound to 127.0.0.1 or using http listener trick
        string? code = null;
        using var listener = new HttpListener();
        listener.Prefixes.Add($"https://127.0.0.1:{port}/");
        try { listener.Start(); }
        catch
        {
            // Fall back to http for local dev; in production use proper HTTPS cert
            // Schwab strictly requires HTTPS, so in production this needs a cert
            _logger.LogWarning("Could not bind HTTPS listener on port {Port}. " +
                "Ensure a certificate is bound (netsh http add sslcert).", port);
            return false;
        }

        // Open browser
        Process.Start(new ProcessStartInfo(authUrl) { UseShellExecute = true });
        _logger.LogInformation("Waiting for OAuth callback on port {Port}...", port);

        var context = await listener.GetContextAsync();
        var query = HttpUtility.ParseQueryString(context.Request.Url?.Query ?? "");
        code = query["code"];

        var response = context.Response;
        var body = "<html><body><h2>FastDOM: Authorization complete. You may close this window.</h2></body></html>"u8.ToArray();
        response.ContentLength64 = body.Length;
        await response.OutputStream.WriteAsync(body, ct);
        response.Close();
        listener.Stop();

        if (string.IsNullOrEmpty(code))
        {
            _logger.LogError("No authorization code received from Schwab");
            return false;
        }

        return await ExchangeCodeForTokensAsync(code, secret, ct);
    }

    private async Task<bool> ExchangeCodeForTokensAsync(string code, string secret, CancellationToken ct)
    {
        var credentials = Convert.ToBase64String(
            Encoding.UTF8.GetBytes($"{_config.AppKey}:{secret}"));

        using var req = new HttpRequestMessage(HttpMethod.Post, _config.TokenUrl);
        req.Headers.Add("Authorization", $"Basic {credentials}");
        req.Content = new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["grant_type"]   = "authorization_code",
            ["code"]         = code,
            ["redirect_uri"] = _config.CallbackUrl
        });

        try
        {
            var resp = await _http.SendAsync(req, ct);
            var body = await resp.Content.ReadAsStringAsync(ct);

            if (!resp.IsSuccessStatusCode)
            {
                _logger.LogError("Token exchange failed: {Status} {Body}", resp.StatusCode, body);
                return false;
            }

            return ParseAndStoreTokens(body);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Token exchange request failed");
            return false;
        }
    }

    public async Task<bool> RefreshTokenAsync(CancellationToken ct = default)
    {
        var refreshToken = _storage.Retrieve(KeyRefreshToken);
        if (string.IsNullOrEmpty(refreshToken))
        {
            _logger.LogWarning("No refresh token stored — full re-auth required");
            return false;
        }

        if (DateTime.UtcNow >= _refreshTokenExpiry)
        {
            _logger.LogWarning("Refresh token expired (7-day limit) — full re-auth required");
            return false;
        }

        var secret = _storage.Retrieve(KeyAppSecret);
        if (string.IsNullOrEmpty(secret)) return false;

        var credentials = Convert.ToBase64String(
            Encoding.UTF8.GetBytes($"{_config.AppKey}:{secret}"));

        using var req = new HttpRequestMessage(HttpMethod.Post, _config.TokenUrl);
        req.Headers.Add("Authorization", $"Basic {credentials}");
        req.Content = new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["grant_type"]    = "refresh_token",
            ["refresh_token"] = refreshToken
        });

        try
        {
            var resp = await _http.SendAsync(req, ct);
            var body = await resp.Content.ReadAsStringAsync(ct);

            if (!resp.IsSuccessStatusCode)
            {
                _logger.LogError("Token refresh failed: {Status} {Body}", resp.StatusCode, body);
                return false;
            }

            return ParseAndStoreTokens(body);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Token refresh request failed");
            return false;
        }
    }

    private bool ParseAndStoreTokens(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;

            var accessToken = root.GetProperty("access_token").GetString() ?? string.Empty;
            var refreshToken = root.GetProperty("refresh_token").GetString() ?? string.Empty;
            var expiresIn = root.GetProperty("expires_in").GetInt32();

            _accessToken = accessToken;
            _accessTokenExpiry = DateTime.UtcNow.AddSeconds(expiresIn);
            _refreshTokenExpiry = DateTime.UtcNow.AddDays(_config.RefreshTokenExpiryDays);

            _storage.Store(KeyAccessToken, accessToken);
            _storage.Store(KeyRefreshToken, refreshToken);
            _storage.Store(KeyTokenExpiry, _accessTokenExpiry.ToString("O"));
            _storage.Store(KeyRefreshExpiry, _refreshTokenExpiry.ToString("O"));

            _logger.LogInformation("Schwab tokens stored. Access expires: {Exp}", _accessTokenExpiry);
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to parse token response");
            return false;
        }
    }

    public async Task<string?> GetAccessTokenAsync(CancellationToken ct = default)
    {
        if (IsAuthenticated) return _accessToken;

        // Try loading from storage
        _accessToken = _storage.Retrieve(KeyAccessToken);
        LoadStoredExpiries();
        if (IsAuthenticated) return _accessToken;

        // Access token expired but refresh token still valid
        if (_storage.Exists(KeyRefreshToken) && DateTime.UtcNow < _refreshTokenExpiry)
        {
            _logger.LogInformation("Access token expired, refreshing...");
            if (await RefreshTokenAsync(ct))
                return _accessToken;
        }

        _logger.LogWarning("No valid access token — authentication required");
        return null;
    }

    public async Task LogoutAsync(CancellationToken ct = default)
    {
        _accessToken = null;
        _accessTokenExpiry = DateTime.MinValue;
        _refreshTokenExpiry = DateTime.MinValue;
        _storage.Delete(KeyAccessToken);
        _storage.Delete(KeyRefreshToken);
        _storage.Delete(KeyTokenExpiry);
        _storage.Delete(KeyRefreshExpiry);
        await Task.CompletedTask;
        _logger.LogInformation("Schwab session cleared");
    }

    public ValueTask DisposeAsync()
    {
        _http.Dispose();
        return ValueTask.CompletedTask;
    }
}
