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
    }

    public Task<DerbyTokenData> GetTokenDataAsync(CancellationToken ct = default)
    {
        // Reading tokens from a thinkorswim Apache Derby database requires the IBM DB2 CLI
        // driver (db2app64.dll) to be installed separately via the full IBM DB2 Client package.
        // The Net.IBM.Data.Db2 NuGet package ships only a skeleton clidriver without the
        // native binaries, which caused a fatal DllNotFoundException in the GC finalizer.
        // The IBM.Data.Db2 dependency has been removed to prevent crashes in non-Schwab modes.
        //
        // To re-enable Derby token reading:
        //   1. Install IBM Data Server Driver: https://www.ibm.com/support/pages/node/323035
        //   2. Re-add Net.IBM.Data.Db2 to FastDOM.Broker.Schwab.csproj
        //   3. Restore the original implementation from git history
        //   4. Ensure IBM_DB_HOME points to the installed client before the assembly loads
        _logger.LogError("Derby token reading is not available — IBM DB2 CLI not installed. " +
                         "Configure a direct OAuth flow or install the IBM DB2 Client.");
        throw new PlatformNotSupportedException(
            "Derby/DB2 token source is not configured. " +
            "The IBM DB2 CLI driver is required but not installed. " +
            "See docs/SchwabIntegration.md for setup instructions.");
    }
}
