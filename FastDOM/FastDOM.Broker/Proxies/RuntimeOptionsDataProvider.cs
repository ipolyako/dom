using FastDOM.MarketData.Interfaces;
using FastDOM.MarketData.Models;
using Microsoft.Extensions.Logging;

namespace FastDOM.Broker.Proxies;

/// <summary>
/// Runtime swapped provider for options chains/expirations.
/// Swap the inner provider when trading mode changes.
/// </summary>
public class RuntimeOptionsDataProvider : IOptionsDataProvider
{
    private readonly ILogger<RuntimeOptionsDataProvider> _logger;
    private IOptionsDataProvider? _inner;

    public RuntimeOptionsDataProvider(ILogger<RuntimeOptionsDataProvider> logger) => _logger = logger;

    public async Task SwapAsync(IOptionsDataProvider options, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();

        if (_inner is IAsyncDisposable ad)
        {
            await ad.DisposeAsync();
        }
        else if (_inner is IDisposable d)
        {
            d.Dispose();
        }

        _inner = options;
        _logger.LogInformation("Options provider swapped to {Type}", options.GetType().Name);
        await Task.CompletedTask;
    }

    public Task<IReadOnlyList<DateOnly>> GetExpirationDatesAsync(string underlying, CancellationToken ct = default)
        => _inner?.GetExpirationDatesAsync(underlying, ct) ??
            Task.FromResult<IReadOnlyList<DateOnly>>([]);

    public Task<IReadOnlyList<OptionsChainRow>> GetChainAsync(string underlying, DateOnly expiration, CancellationToken ct = default)
        => _inner?.GetChainAsync(underlying, expiration, ct) ??
            Task.FromResult<IReadOnlyList<OptionsChainRow>>([]);
}
