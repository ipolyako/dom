using System.IO;
using System.Text;
using System.Text.Json;
using FastDOM.Broker.Interfaces;
using FastDOM.Infrastructure.Config;
using Microsoft.Extensions.Logging;
using System.Diagnostics;

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
    private readonly TokenSourceConfig _tokenSource;
    private readonly HttpClient _http;

    private string? _accessToken;
    private string? _accountHash;
    private DateTime _accessTokenExpiry = DateTime.MinValue;

    public bool IsAuthenticated =>
        !string.IsNullOrEmpty(_accessToken) && DateTime.UtcNow < _accessTokenExpiry;

    public string? AccountHash => _accountHash;

    public DateTime? TokenExpiresAt =>
        _accessTokenExpiry == DateTime.MinValue ? null : _accessTokenExpiry;

    // Tokens are always exchanged on demand. Derby auth can still be sourced through JDBC bridge.
    public DateTime? RefreshTokenExpiresAt => null;

    public bool NeedsReauth => !IsAuthenticated;

    public SchwabAuthProvider(
        ILogger<SchwabAuthProvider> logger,
        SchwabConfig schwabConfig,
        DerbyTokenProvider derby,
        TokenSourceConfig tokenSource)
    {
        _logger      = logger;
        _schwabConfig = schwabConfig;
        _derby       = derby;
        _tokenSource = tokenSource;
        _http        = new HttpClient();
    }

    public async Task<bool> LoginAsync(CancellationToken ct = default)
    {
        // 0. File-based override (gitignored). Used on machines that can't reach
        //    the token DB (dev laptops away from corp network). If the override
        //    file exists AND parses, take that path and skip DB / Python bridge.
        //    File location: <exeDir>/schwab.token.override.json
        //    Schema: { "appKey": "...", "appSecret": "...", "refreshToken": "...", "accountHash": "..." (optional) }
        var overrideResult = await TryFileOverrideAsync(ct);
        if (overrideResult.HasValue) return overrideResult.Value;

        // 1. Java/AgentQuant bridge is the primary Derby path. The DB2 .NET
        //    provider is kept only as a fallback for older deployments.
        if (await TryAgentQuantBridgeAsync(ct))
            return true;

        // 2. Direct Derby / DB2 lookup.
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

    // Returns null when the override file is absent → other paths should run.
    // Returns true/false when the override was used, so caller returns that result.
    private async Task<bool?> TryFileOverrideAsync(CancellationToken ct)
    {
        var path = Path.Combine(AppContext.BaseDirectory, "schwab.token.override.json");
        if (!File.Exists(path)) return null;

        try
        {
            var json = await File.ReadAllTextAsync(path, ct);
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            string Get(string name) => root.TryGetProperty(name, out var el) && el.ValueKind == JsonValueKind.String
                                       ? el.GetString() ?? "" : "";
            var appKey       = Get("appKey");
            var appSecret    = Get("appSecret");
            var refreshToken = Get("refreshToken");
            var accountHash  = Get("accountHash");
            if (string.IsNullOrWhiteSpace(appKey) || string.IsNullOrWhiteSpace(refreshToken))
            {
                _logger.LogWarning("schwab.token.override.json present but missing appKey or refreshToken");
                return null;
            }
            _logger.LogInformation("Using schwab.token.override.json for {Purpose} authentication (DB/bridge skipped)", _tokenSource.Purpose);
            if (!string.IsNullOrEmpty(accountHash)) _accountHash = accountHash;
            return await ExchangeRefreshTokenAsync(appKey, appSecret, refreshToken, ct);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to apply schwab.token.override.json");
            return null;
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

            _logger.LogInformation("Schwab {Purpose} access token obtained. Expires: {Exp}", _tokenSource.Purpose, _accessTokenExpiry);
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Schwab token exchange request failed");
            return false;
        }
    }

    private async Task<bool> TryAgentQuantBridgeAsync(CancellationToken ct)
    {
        var scriptPath = ResolveAgentQuantScriptPath();
        if (string.IsNullOrWhiteSpace(scriptPath))
        {
            _logger.LogWarning("Agentquant bridge script was not found.");
            return false;
        }

        var python = ResolvePythonExe();
        if (string.IsNullOrWhiteSpace(python))
        {
            _logger.LogWarning("Python executable not found; cannot run agentquant bridge");
            return false;
        }

        var args = BuildBridgeArguments(scriptPath);
        var psi = new ProcessStartInfo
        {
            FileName = python,
            Arguments = args,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };

        foreach (var kvp in BuildBridgeEnvironment())
        {
            psi.Environment[kvp.Key] = kvp.Value;
        }

        try
        {
            using var proc = Process.Start(psi);
            if (proc == null)
            {
                _logger.LogWarning("Could not start Python bridge process");
                return false;
            }

            var output = await proc.StandardOutput.ReadToEndAsync();
            var error = await proc.StandardError.ReadToEndAsync();
            await proc.WaitForExitAsync(ct);

            if (proc.ExitCode != 0)
            {
                _logger.LogWarning("Agentquant bridge exited with code {Code}: {Error}", proc.ExitCode, error);
                return false;
            }

            return TryApplyBridgeToken(output);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Agentquant bridge execution failed");
            return false;
        }
    }

    private string ResolveAgentQuantScriptPath()
    {
        var localBridge = Path.Combine(AppContext.BaseDirectory, "scripts", "fastdom_schwab_token_bridge.py");
        if (File.Exists(localBridge)) return localBridge;

        var devBridge = Path.Combine(AppContext.BaseDirectory, "..", "scripts", "fastdom_schwab_token_bridge.py");
        if (File.Exists(devBridge))
            return Path.GetFullPath(devBridge);

        var configuredRoot = Environment.GetEnvironmentVariable("FASTDOM_AGENTQUANT_ROOT");
        if (!string.IsNullOrWhiteSpace(configuredRoot))
        {
            var configuredPath = Path.Combine(configuredRoot, "scripts", "test_fastdom_schwab_from_agentquant.py");
            if (File.Exists(configuredPath)) return Path.GetFullPath(configuredPath);

            var legacyPath = Path.Combine(configuredRoot, "scripts", "test_schwab_auth.py");
            if (File.Exists(legacyPath)) return Path.GetFullPath(legacyPath);
        }

        var local = Path.Combine(AppContext.BaseDirectory, "scripts", "test_fastdom_schwab_from_agentquant.py");
        if (File.Exists(local)) return local;

        var dev = Path.Combine(AppContext.BaseDirectory, "..", "scripts", "test_fastdom_schwab_from_agentquant.py");
        if (File.Exists(dev))
            return Path.GetFullPath(dev);

        return "";
    }

    private static string ResolvePythonExe()
    {
        var explicitPython = Environment.GetEnvironmentVariable("FASTDOM_PYTHON_EXE");
        if (!string.IsNullOrWhiteSpace(explicitPython))
            return explicitPython;

        return "python";
    }

    private string BuildBridgeArguments(string scriptPath)
    {
        var args = new List<string>
        {
            QuoteArg(scriptPath),
            "--include-access-token",
            "--derby-jdbc-url",
            QuoteArg(BuildDerbyJdbcUrl()),
            "--derby-user",
            QuoteArg(_tokenSource.User),
            "--derby-password",
            QuoteArg(_tokenSource.Password),
            "--schwab-purpose",
            QuoteArg(_tokenSource.Purpose)
        };

        if (!string.IsNullOrWhiteSpace(_tokenSource.AccountId))
        {
            args.Add("--schwab-account-id");
            args.Add(QuoteArg(_tokenSource.AccountId));
        }

        if (!string.IsNullOrWhiteSpace(_tokenSource.Schema))
        {
            args.Add("--derby-schema");
            args.Add(QuoteArg(_tokenSource.Schema));
        }

        var agentquantRoot = Environment.GetEnvironmentVariable("FASTDOM_AGENTQUANT_ROOT");
        if (!string.IsNullOrWhiteSpace(agentquantRoot))
        {
            args.Add("--agentquant-root");
            args.Add(QuoteArg(agentquantRoot));
        }

        return string.Join(" ", args);
    }

    private Dictionary<string, string> BuildBridgeEnvironment()
    {
        var env = new Dictionary<string, string>();

        if (!string.IsNullOrWhiteSpace(_tokenSource.User))
            env["DERBY_USER"] = _tokenSource.User;
        if (!string.IsNullOrWhiteSpace(_tokenSource.Password))
            env["DERBY_PASSWORD"] = _tokenSource.Password;
        if (!string.IsNullOrWhiteSpace(_tokenSource.Purpose))
            env["SCHWAB_AUTH_PURPOSE"] = _tokenSource.Purpose;
        if (!string.IsNullOrWhiteSpace(_tokenSource.AccountId))
            env["SCHWAB_ACCOUNT_ID"] = _tokenSource.AccountId;
        if (!string.IsNullOrWhiteSpace(_tokenSource.Schema))
            env["SCHWAB_AUTH_SCHEMA"] = _tokenSource.Schema;
        env["SCHWAB_AUTH_SOURCE"] = "derby";

        return env;
    }

    private string BuildDerbyJdbcUrl()
    {
        var host = string.IsNullOrWhiteSpace(_tokenSource.Host) ? "localhost" : _tokenSource.Host;
        var db = string.IsNullOrWhiteSpace(_tokenSource.Database) ? "tradedb" : _tokenSource.Database;
        return $"jdbc:derby://{host}:{_tokenSource.Port}/{db};create=false";
    }

    private static string QuoteArg(string value) => $"\"{value.Replace("\"", "\\\"")}\"";

    private bool TryApplyBridgeToken(string output)
    {
        if (string.IsNullOrWhiteSpace(output))
        {
            _logger.LogWarning("Agentquant bridge returned empty output");
            return false;
        }

        try
        {
            using var doc = JsonDocument.Parse(output);
            var root = doc.RootElement;
            if (root.TryGetProperty("status", out var status) && status.GetString() == "ok")
            {
                var token = root.TryGetProperty("access_token", out var tokenEl) ? tokenEl.GetString() : "";
                if (string.IsNullOrWhiteSpace(token))
                {
                    _logger.LogWarning("Agentquant bridge success payload did not include access_token");
                    return false;
                }

                _accessToken = token;
                var expiresIn = root.TryGetProperty("expires_in", out var exp) && exp.ValueKind == JsonValueKind.Number
                    ? exp.GetInt32()
                    : _schwabConfig.AccessTokenExpirySeconds;
                _accessTokenExpiry = DateTime.UtcNow.AddSeconds(expiresIn - 60);
                _logger.LogInformation(
                    "Schwab {Purpose} access token obtained from agentquant bridge. Expires: {Exp}",
                    _tokenSource.Purpose,
                    _accessTokenExpiry);
                return true;
            }
        }
        catch (JsonException ex)
        {
            _logger.LogError(ex, "Failed to parse agentquant bridge output");
        }

        _logger.LogWarning("Agentquant bridge output did not return a usable token");
        return false;
    }

    public async Task<string?> GetAccessTokenAsync(CancellationToken ct = default)
    {
        if (IsAuthenticated) return _accessToken;

        _logger.LogInformation("Access token expired or missing — fetching new access token...");
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
