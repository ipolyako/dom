using System.Text.Json;
using FastDOM.Core.Models;
using Microsoft.Extensions.Logging;

namespace FastDOM.Infrastructure.Logging;

public class AuditLogger
{
    private readonly ILogger<AuditLogger> _logger;
    private readonly string _auditPath;
    private readonly SemaphoreSlim _lock = new(1, 1);

    public AuditLogger(ILogger<AuditLogger> logger, string logDirectory)
    {
        _logger = logger;
        Directory.CreateDirectory(logDirectory);
        _auditPath = Path.Combine(logDirectory, $"audit_{DateTime.Now:yyyyMMdd}.jsonl");
    }

    public async Task LogOrderSubmittedAsync(OrderRequest request, long clickToSubmitMs)
    {
        var entry = new AuditEntry
        {
            EventType = "ORDER_SUBMITTED",
            AccountId = request.AccountId,
            Symbol = request.Symbol,
            Source = request.Source.ToString(),
            Data = new Dictionary<string, object?>
            {
                ["clientOrderId"] = request.ClientOrderId,
                ["side"] = request.Side.ToString(),
                ["qty"] = request.Quantity,
                ["orderType"] = request.OrderType.ToString(),
                ["limitPrice"] = request.LimitPrice,
                ["stopPrice"] = request.StopPrice,
                ["source"] = request.Source.ToString(),
                ["clickToSubmitMs"] = clickToSubmitMs,
            }
        };
        await WriteAsync(entry);
    }

    public async Task LogOrderResultAsync(bool success, string? brokerOrderId, string? errorMessage,
        int? httpStatusCode, string clientOrderId, long submitToAckMs)
    {
        var entry = new AuditEntry
        {
            EventType = success ? "ORDER_ACCEPTED" : "ORDER_REJECTED",
            Data = new Dictionary<string, object?>
            {
                ["clientOrderId"] = clientOrderId,
                ["brokerOrderId"] = brokerOrderId,
                ["success"] = success,
                ["error"] = errorMessage,
                ["httpStatus"] = httpStatusCode,
                ["submitToAckMs"] = submitToAckMs,
            }
        };
        await WriteAsync(entry);
    }

    public async Task LogOrderStateChangeAsync(OrderState state)
    {
        var entry = new AuditEntry
        {
            EventType = "ORDER_STATE_CHANGE",
            AccountId = state.AccountId,
            Symbol = state.Symbol,
            Data = new Dictionary<string, object?>
            {
                ["clientOrderId"] = state.ClientOrderId,
                ["brokerOrderId"] = state.BrokerOrderId,
                ["status"] = state.Status.ToString(),
                ["qtyFilled"] = state.QuantityFilled,
                ["avgFill"] = state.AverageFillPrice,
                ["reason"] = state.RejectReason,
            }
        };
        await WriteAsync(entry);
    }

    public async Task LogRiskRejectAsync(string reason, string? accountId, string? symbol, string? source)
    {
        var entry = new AuditEntry
        {
            EventType = "RISK_REJECT",
            AccountId = accountId,
            Symbol = symbol,
            Source = source,
            Data = new Dictionary<string, object?> { ["reason"] = reason }
        };
        await WriteAsync(entry);
    }

    public async Task LogKillSwitchAsync(string accountId, string? symbol, string action)
    {
        var entry = new AuditEntry
        {
            EventType = "KILL_SWITCH",
            AccountId = accountId,
            Symbol = symbol,
            Data = new Dictionary<string, object?> { ["action"] = action }
        };
        await WriteAsync(entry);
        _logger.LogWarning("KILL SWITCH ACTIVATED: Account={Account} Symbol={Symbol} Action={Action}",
            accountId, symbol, action);
    }

    private async Task WriteAsync(AuditEntry entry)
    {
        await _lock.WaitAsync();
        try
        {
            var line = JsonSerializer.Serialize(entry, new JsonSerializerOptions { WriteIndented = false });
            await File.AppendAllTextAsync(_auditPath, line + Environment.NewLine);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to write audit log entry");
        }
        finally
        {
            _lock.Release();
        }
    }
}

public class AuditEntry
{
    public string TimestampUtc { get; init; } = DateTime.UtcNow.ToString("O");
    public string TimestampLocal { get; init; } = DateTime.Now.ToString("O");
    public required string EventType { get; init; }
    public string? AccountId { get; init; }
    public string? Symbol { get; init; }
    public string? Source { get; init; }
    public Dictionary<string, object?> Data { get; init; } = [];
}
