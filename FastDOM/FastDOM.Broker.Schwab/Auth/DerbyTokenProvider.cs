using System.Data;
using System.IO;
using System.Reflection;
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
    private const string Purpose = "DATA";

    private readonly ILogger<DerbyTokenProvider> _logger;
    private readonly TokenSourceConfig _cfg;

    public DerbyTokenProvider(ILogger<DerbyTokenProvider> logger, TokenSourceConfig cfg)
    {
        _logger = logger;
        _cfg    = cfg;
    }

    public Task<DerbyTokenData> GetTokenDataAsync(CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();

        try
        {
            var db2Conn = ResolveDb2Connection();
            using var conn = (IDbConnection)db2Conn;
            conn.Open();

            var auth = LoadAuthValues(conn, ct);
            if (string.IsNullOrWhiteSpace(auth.AppKey))
            {
                throw new InvalidOperationException("No app key found in AUTHREF table");
            }

            if (string.IsNullOrWhiteSpace(auth.AppSecret))
            {
                _logger.LogWarning("APPSECRET was empty. Schwab token exchange may fail.");
            }

            var refreshToken = LoadRefreshToken(conn, auth.AppKey, ct);
            if (string.IsNullOrWhiteSpace(refreshToken))
            {
                throw new InvalidOperationException(
                    $"No refresh token found in {_cfg.Schema}.{_cfg.TokenTable} for ACCOUNTID {auth.AppKey}");
            }

            return Task.FromResult(new DerbyTokenData
            {
                AppKey       = auth.AppKey,
                AppSecret    = auth.AppSecret,
                AccountHash  = auth.AccountHash,
                RefreshToken = refreshToken,
            });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Unable to load Schwab token data from Derby");
            throw;
        }
    }

    private (string AppKey, string AppSecret, string AccountHash) LoadAuthValues(IDbConnection conn, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();

        var columns = new[] { _cfg.AppKeyColumn, _cfg.AppSecretColumn, _cfg.AccountHashColumn };
        var authRefTable = $"{_cfg.Schema}.{_cfg.AuthRefTable}";

        var byPurpose = new CommandSpec(
            $"SELECT {string.Join(", ", columns)} FROM {authRefTable} WHERE PURPOSE = ? FETCH FIRST ROW ONLY",
            Purpose);
        var fallback = new CommandSpec(
            $"SELECT {string.Join(", ", columns)} FROM {authRefTable} ORDER BY RECORDTIME DESC FETCH FIRST ROW ONLY");
        var fallbackNoOrder = new CommandSpec(
            $"SELECT {string.Join(", ", columns)} FROM {authRefTable} FETCH FIRST ROW ONLY");

        var row = TryReadRow(conn, byPurpose)
                  ?? TryReadRow(conn, fallback)
                  ?? TryReadRow(conn, fallbackNoOrder);
        if (row == null)
            throw new InvalidOperationException($"Could not read auth values from {_cfg.Schema}.{_cfg.AuthRefTable}");

        return (
            row.GetValue(_cfg.AppKeyColumn),
            row.GetValue(_cfg.AppSecretColumn),
            row.GetValue(_cfg.AccountHashColumn)
        );
    }

    private string LoadRefreshToken(IDbConnection conn, string accountId, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();

        var tokenTable = $"{_cfg.Schema}.{_cfg.TokenTable}";

        foreach (var column in DistinctColumns(_cfg.RefreshTokenColumn, "TOKEN"))
        {
            var row = TryReadRow(conn, new CommandSpec(
                $"SELECT {column} FROM {tokenTable} WHERE ACCOUNTID = ? ORDER BY RECORDTIME DESC FETCH FIRST ROW ONLY",
                accountId))
                      ?? TryReadRow(conn, new CommandSpec(
                $"SELECT {column} FROM {tokenTable} WHERE ACCOUNTID = ? FETCH FIRST ROW ONLY",
                accountId));

            var value = row?.GetValue(column);
            if (!string.IsNullOrWhiteSpace(value))
                return value;
        }

        return "";
    }

    private IEnumerable<string> DistinctColumns(params string[] columns)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var c in columns)
        {
            if (string.IsNullOrWhiteSpace(c)) continue;
            if (seen.Add(c)) yield return c;
        }
    }

    private DerbyTokenRow? TryReadRow(IDbConnection conn, CommandSpec spec)
    {
        try
        {
            using var cmd = conn.CreateCommand();
            cmd.CommandText = spec.Sql;

            foreach (var value in spec.Parameters)
            {
                var p = cmd.CreateParameter();
                p.ParameterName = "P";
                p.Value = value;
                cmd.Parameters.Add(p);
            }

            using var reader = cmd.ExecuteReader(CommandBehavior.SingleRow);
            if (!reader.Read()) return null;

            var map = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
            for (var i = 0; i < reader.FieldCount; i++)
            {
                map[reader.GetName(i)] = reader.IsDBNull(i) ? null : reader.GetValue(i)?.ToString();
            }
            return new DerbyTokenRow(map);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Auth query failed: {Sql}", spec.Sql);
            return null;
        }
    }

    private IDbConnection ResolveDb2Connection()
    {
        EnsureDb2ProviderLoaded();

        var connTypeNames = new[]
        {
            "IBM.Data.DB2.DB2Connection, IBM.Data.DB2",
            "IBM.Data.Db2.DB2Connection, IBM.Data.DB2",
            "IBM.Data.DB2.Core.DB2Connection, IBM.Data.DB2.Core",
            "IBM.Data.DB2.Core.DB2Connection, Net.IBM.Data.Db2",
        };

        Type? connectionType = null;
        foreach (var name in connTypeNames)
        {
            connectionType = Type.GetType(name, throwOnError: false);
            if (connectionType != null) break;
        }

        if (connectionType == null)
            throw new InvalidOperationException(
                "Could not load IBM DB2 connection type. Add Net.IBM.Data.Db2 (or IBM.Data.DB2.Core) package and restore dependencies.");

        var connectionString = BuildConnectionString();
        var instance = Activator.CreateInstance(connectionType, connectionString);
        if (instance is not IDbConnection conn)
            throw new InvalidOperationException($"Resolved type {connectionType.FullName} is not an IDbConnection.");

        return conn;
    }

    private static void EnsureDb2ProviderLoaded()
    {
        if (Type.GetType("IBM.Data.Db2.DB2Connection") != null) return;

        var possibleDlls = new[]
        {
            Path.Combine(AppContext.BaseDirectory, "IBM.Data.Db2.dll"),
            Path.Combine(AppContext.BaseDirectory, "rebuild", "IBM.Data.Db2.dll")
        };

        foreach (var localDll in possibleDlls)
        {
            if (!File.Exists(localDll)) continue;

            try
            {
                Assembly.LoadFrom(localDll);
                if (Type.GetType("IBM.Data.Db2.DB2Connection") != null) return;
            }
            catch (Exception ex)
            {
                throw new InvalidOperationException(
                    $"Could not load IBM DB2 provider assembly from {localDll}.", ex);
            }
        }
    }

    private string BuildConnectionString()
    {
        var host = string.IsNullOrWhiteSpace(_cfg.Host) ? "localhost" : _cfg.Host;
        return $"Server={host}:{_cfg.Port};Database={_cfg.Database};UID={_cfg.User};PWD={_cfg.Password};Connect Timeout=10;Pooling=false;";
    }

    private class DerbyTokenRow(Dictionary<string, string?> map)
    {
        public string GetValue(string key)
            => map.TryGetValue(key, out var value) ? value ?? "" : "";
    }

    private record CommandSpec(string Sql, params string[] Parameters)
    {
        public CommandSpec(string sql) : this(sql, Array.Empty<string>()) { }
    }
}

