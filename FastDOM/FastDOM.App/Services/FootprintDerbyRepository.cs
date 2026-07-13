using System.Diagnostics;
using System.IO;
using System.Text.Json;
using FastDOM.Infrastructure.Config;
using Microsoft.Extensions.Logging;

namespace FastDOM.App.Services;

public sealed record FootprintPersistRow(string Symbol, DateTime BarTimeUtc, decimal Price,
    long BidVolume, long AskVolume, long UnknownVolume, int TradeCount);

public sealed class FootprintDerbyRepository
{
    private readonly ILogger<FootprintDerbyRepository> _logger;
    private readonly TokenSourceConfig _config;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public FootprintDerbyRepository(ILogger<FootprintDerbyRepository> logger, ConfigManager config)
    {
        _logger = logger;
        _config = config.TokenSource;
    }

    public Task UpsertAsync(IReadOnlyCollection<FootprintPersistRow> rows, CancellationToken ct) =>
        rows.Count == 0 ? Task.CompletedTask : RunAsync("upsert", rows, ct);

    public async Task<IReadOnlyList<FootprintPersistRow>> LoadAsync(string symbol, int limit, CancellationToken ct)
    {
        var result = await RunAsync("load", new { symbol, limit }, ct);
        if (string.IsNullOrWhiteSpace(result)) return [];
        using var doc = JsonDocument.Parse(result);
        if (!doc.RootElement.TryGetProperty("rows", out var rows)) return [];
        return JsonSerializer.Deserialize<List<FootprintPersistRow>>(rows.GetRawText(), JsonOptions) ?? [];
    }

    private async Task<string> RunAsync(string action, object payload, CancellationToken ct)
    {
        await _gate.WaitAsync(ct);
        try
        {
            var script = Path.Combine(AppContext.BaseDirectory, "scripts", "fastdom_footprint_derby.py");
            if (!File.Exists(script)) throw new FileNotFoundException("Footprint Derby bridge not found", script);
            var psi = new ProcessStartInfo
            {
                FileName = "python",
                Arguments = $"\"{script}\" --action {action}",
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
                WindowStyle = ProcessWindowStyle.Hidden
            };
            psi.Environment["DERBY_JDBC_URL"] = BuildJdbcUrl();
            psi.Environment["DERBY_USER"] = _config.User;
            psi.Environment["DERBY_PASSWORD"] = _config.Password;
            psi.Environment["DERBY_AUTH_SCHEMA"] = string.IsNullOrWhiteSpace(_config.Schema) ? "ROCH" : _config.Schema;
            psi.Environment["FASTDOM_AGENTQUANT_ROOT"] = @"E:\AIWork\agentquant";

            using var process = Process.Start(psi) ?? throw new InvalidOperationException("Could not start Derby bridge");
            await process.StandardInput.WriteAsync(JsonSerializer.Serialize(payload, JsonOptions));
            process.StandardInput.Close();
            var stdoutTask = process.StandardOutput.ReadToEndAsync(ct);
            var stderrTask = process.StandardError.ReadToEndAsync(ct);
            await process.WaitForExitAsync(ct);
            var stdout = await stdoutTask;
            var stderr = await stderrTask;
            if (process.ExitCode != 0)
                throw new InvalidOperationException($"Derby bridge failed ({process.ExitCode}): {stderr}");
            _logger.LogDebug("Footprint Derby {Action} completed", action);
            return stdout;
        }
        finally { _gate.Release(); }
    }

    private string BuildJdbcUrl()
    {
        var host = string.IsNullOrWhiteSpace(_config.Host) ? "localhost" : _config.Host;
        var database = string.IsNullOrWhiteSpace(_config.Database) ? "tradedb" : _config.Database;
        return $"jdbc:derby://{host}:{_config.Port}/{database};create=false";
    }
}
