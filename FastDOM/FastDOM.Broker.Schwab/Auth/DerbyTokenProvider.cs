using IBM.Data.Db2;
using FastDOM.Infrastructure.Config;
using Microsoft.Extensions.Logging;

namespace FastDOM.Broker.Schwab.Auth;

public class DerbyTokenData
{
    public string AppKey       { get; init; } = "";
    public string AppSecret    { get; init; } = "";
    public string AccountHash  { get; init; } = "";
    public string RefreshToken { get; init; } = "";
}

public class DerbyTokenProvider
{
    private readonly ILogger<DerbyTokenProvider> _logger;
    private readonly TokenSourceConfig _cfg;

    public DerbyTokenProvider(ILogger<DerbyTokenProvider> logger, TokenSourceConfig cfg)
    {
        _logger = logger;
        _cfg = cfg;
        EnsureIbmHome();
    }

    private static void EnsureIbmHome()
    {
        // IBM driver requires IBM_DB_HOME to point to the clidriver folder
        if (string.IsNullOrEmpty(Environment.GetEnvironmentVariable("IBM_DB_HOME")))
        {
            var clidriver = Path.Combine(AppContext.BaseDirectory, "clidriver");
            if (Directory.Exists(clidriver))
            {
                Environment.SetEnvironmentVariable("IBM_DB_HOME", clidriver);
                var path = Environment.GetEnvironmentVariable("PATH") ?? "";
                var bin  = Path.Combine(clidriver, "bin");
                if (!path.Contains(bin, StringComparison.OrdinalIgnoreCase))
                    Environment.SetEnvironmentVariable("PATH", $"{bin};{path}");
            }
        }
    }

    public async Task<DerbyTokenData> GetTokenDataAsync(CancellationToken ct = default)
    {
        var connStr = $"Server={_cfg.Host}:{_cfg.Port};Database={_cfg.Database};" +
                      $"UID={_cfg.User};PWD={_cfg.Password};";

        _logger.LogInformation("Fetching Schwab token from Derby {Host}:{Port}/{Db}",
            _cfg.Host, _cfg.Port, _cfg.Database);

        return await Task.Run(() =>
        {
            using var conn = new DB2Connection(connStr);
            conn.Open();

            // Read appKey, appSecret, accountHash from AUTHREF
            string appKey = "", appSecret = "", accountHash = "";
            using (var cmd = conn.CreateCommand())
            {
                cmd.CommandText =
                    $"SELECT {_cfg.AppKeyColumn}, {_cfg.AppSecretColumn}, {_cfg.AccountHashColumn} " +
                    $"FROM {_cfg.Schema}.{_cfg.AuthRefTable} FETCH FIRST 1 ROWS ONLY";
                using var r = cmd.ExecuteReader();
                if (!r.Read())
                    throw new InvalidOperationException($"No rows in {_cfg.Schema}.{_cfg.AuthRefTable}");
                appKey      = r.GetString(0);
                appSecret   = r.GetString(1);
                accountHash = r.GetString(2);
            }

            // Read refresh token from TOKEN keyed by appKey
            string refreshToken = "";
            using (var cmd = conn.CreateCommand())
            {
                cmd.CommandText =
                    $"SELECT {_cfg.RefreshTokenColumn} FROM {_cfg.Schema}.{_cfg.TokenTable} " +
                    $"WHERE {_cfg.AppKeyColumn} = ? FETCH FIRST 1 ROWS ONLY";
                var p = cmd.CreateParameter();
                p.Value = appKey;
                cmd.Parameters.Add(p);
                using var r = cmd.ExecuteReader();
                if (!r.Read())
                    throw new InvalidOperationException($"No token row in {_cfg.Schema}.{_cfg.TokenTable} for appKey");
                refreshToken = r.GetString(0);
            }

            _logger.LogInformation("Derby token data fetched. AccountHash: {Hash}",
                accountHash.Length > 6 ? accountHash[..6] + "..." : "***");

            return new DerbyTokenData
            {
                AppKey       = appKey,
                AppSecret    = appSecret,
                AccountHash  = accountHash,
                RefreshToken = refreshToken,
            };
        }, ct);
    }
}
