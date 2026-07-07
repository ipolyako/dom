using System.Text;
using System.Text.Json;
using FastDOM.Broker.Interfaces;
using FastDOM.Infrastructure.Config;
using Microsoft.Extensions.Logging;

namespace FastDOM.Broker.Schwab.Auth;

/// <summary>
/// Authenticates with the Schwab Trader API using a refresh token sourced from Derby.
/// Flow: Derby ROCH.AUTHREF → appKey/appSecret/accountHash
///       Derby ROCH.TOKEN   → refreshToken
///       Schwab token endpoint → access token (valid 30 min)
/// </summary>
public class SchwabAuthProvider : IAuthProvider
{
    private readonly ILogger<SchwabAuthProvider> _logger;
    private readonly SchwabConfig _schwabConfig;
    private readonly DerbyTokenProvider _derby;
    private readonly HttpClient _http;

    private string? _accessToken;
    private string? _accountHash;
    private DateTime _accessTokenExpiry = DateTime.MinValue;

    public bool IsAuthenticated =>
        !string.IsNullOrEmpty(_accessToken) && DateTime.UtcNow < _accessTokenExpiry;

    public string? AccountHash => _accountHash;

    public DateTime? TokenExpiresAt =>
        _accessTokenExpiry == DateTime.MinValue ? null : _accessTokenExpiry;

    // Derby tokens are always re-fetched on demand — no concept of refresh token expiry here
    public DateTime? RefreshTokenExpiresAt => null;

    public bool NeedsReauth => !IsAuthenticated;

    public SchwabAuthProvider(
        ILogger<SchwabAuthProvider> logger,
        SchwabConfig schwabConfig,
        DerbyTokenProvider derby)
    {
        _logger      = logger;
        _schwabConfig = schwabConfig;
        _derby       = derby;
        _http        = new HttpClient();
    }

    public async Task<bool> LoginAsync(CancellationToken ct = default)
    {
        try
        {
            var data = await _derby.GetTokenDataAsync(ct);
            _accountHash = data.AccountHash;
            return await ExchangeRefreshTokenAsync(data.AppKey, data.AppSecret, data.RefreshToken, ct);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to authenticate via Derby token provider");
            return false;
        }
    }

    public async Task<bool> RefreshTokenAsync(CancellationToken ct = default)
        => await LoginAsync(ct);

    private async Task<bool> ExchangeRefreshTokenAsync(
        string appKey, string appSecret, string refreshToken, CancellationToken ct)
    {
        var credentials = Convert.ToBase64String(Encoding.UTF8.GetBytes($"{appKey}:{appSecret}"));

        using var req = new HttpRequestMessage(HttpMethod.Post, _schwabConfig.TokenUrl);
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
                _logger.LogError("Schwab token exchange failed: {Status} {Body}", resp.StatusCode, body);
                return false;
            }

            using var doc = JsonDocument.Parse(body);
            var root = doc.RootElement;
            _accessToken       = root.GetProperty("access_token").GetString();
            var expiresIn      = root.GetProperty("expires_in").GetInt32();
            _accessTokenExpiry = DateTime.UtcNow.AddSeconds(expiresIn - 60); // 60s buffer

            _logger.LogInformation("Schwab access token obtained. Expires: {Exp}", _accessTokenExpiry);
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Schwab token exchange request failed");
            return false;
        }
    }

    public async Task<string?> GetAccessTokenAsync(CancellationToken ct = default)
    {
        if (IsAuthenticated) return _accessToken;

        _logger.LogInformation("Access token expired or missing — fetching from Derby...");
        if (await LoginAsync(ct)) return _accessToken;

        _logger.LogWarning("Could not obtain Schwab access token");
        return null;
    }

    public async Task LogoutAsync(CancellationToken ct = default)
    {
        _accessToken = null;
        _accessTokenExpiry = DateTime.MinValue;
        await Task.CompletedTask;
        _logger.LogInformation("Schwab session cleared");
    }

    public ValueTask DisposeAsync()
    {
        _http.Dispose();
        return ValueTask.CompletedTask;
    }
}
