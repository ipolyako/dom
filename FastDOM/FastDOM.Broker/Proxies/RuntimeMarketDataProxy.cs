using System.Reactive.Subjects;
using FastDOM.MarketData.Interfaces;
using FastDOM.MarketData.Models;
using Microsoft.Extensions.Logging;

namespace FastDOM.Broker.Proxies;

/// <summary>
/// Singleton DI entry-point for the active market data client.
/// Swap the inner client at runtime via SwapAsync without rebuilding the DI container.
/// </summary>
public class RuntimeMarketDataProxy : IMarketDataClient
{
    private readonly ILogger<RuntimeMarketDataProxy> _logger;
    private readonly Subject<Quote> _quoteSubject = new();
    private readonly Subject<MarketDepth> _depthSubject = new();
    private readonly Subject<Trade> _tradeSubject = new();
    private readonly Subject<AccountActivity> _accountActivitySubject = new();
    private readonly Subject<bool> _connectionSubject = new();
    private IDisposable? _quoteSub;
    private IDisposable? _depthSub;
    private IDisposable? _tradeSub;
    private IDisposable? _accountActivitySub;
    private IDisposable? _connSub;
    private IMarketDataClient? _inner;
    private readonly List<string> _quoteSubs = [];
    private readonly List<string> _depthSubs = [];

    public bool IsConnected => _inner?.IsConnected ?? false;
    public bool SupportsLevelTwo => _inner?.SupportsLevelTwo ?? false;
    public IObservable<Quote> QuoteStream => _quoteSubject;
    public IObservable<MarketDepth> DepthStream => _depthSubject;
    public IObservable<Trade> TradeStream => _tradeSubject;
    public IObservable<AccountActivity> AccountActivityStream => _accountActivitySubject;
    public IObservable<bool> ConnectionStateStream => _connectionSubject;

    public RuntimeMarketDataProxy(ILogger<RuntimeMarketDataProxy> logger) => _logger = logger;

    public async Task SwapAsync(IMarketDataClient newClient, CancellationToken ct = default)
    {
        if (_inner != null)
        {
            _quoteSub?.Dispose();
            _depthSub?.Dispose();
            _tradeSub?.Dispose();
            _accountActivitySub?.Dispose();
            _connSub?.Dispose();
            try { await _inner.DisconnectAsync(ct); } catch { /* best-effort */ }
            await _inner.DisposeAsync();
        }

        _inner = newClient;
        _quoteSub = newClient.QuoteStream.Subscribe(_quoteSubject);
        _depthSub = newClient.DepthStream.Subscribe(_depthSubject);
        _tradeSub = newClient.TradeStream.Subscribe(_tradeSubject);
        _accountActivitySub = newClient.AccountActivityStream.Subscribe(_accountActivitySubject);
        _connSub  = newClient.ConnectionStateStream.Subscribe(_connectionSubject);
        _logger.LogInformation("MarketData swapped to {Type}", newClient.GetType().Name);

        // Re-subscribe to symbols that were active before the swap
        foreach (var s in _quoteSubs.ToList())
            await newClient.SubscribeQuotesAsync(s, ct);
        foreach (var s in _depthSubs.ToList())
            await newClient.SubscribeDepthAsync(s, ct);
    }

    public Task ConnectAsync(CancellationToken ct = default)
        => _inner?.ConnectAsync(ct) ?? Task.CompletedTask;

    public Task DisconnectAsync(CancellationToken ct = default)
        => _inner?.DisconnectAsync(ct) ?? Task.CompletedTask;

    public Task<Quote?> GetSnapshotAsync(string symbol, CancellationToken ct = default)
        => _inner?.GetSnapshotAsync(symbol, ct) ?? Task.FromResult<Quote?>(null);

    public async Task SubscribeQuotesAsync(string symbol, CancellationToken ct = default)
    {
        if (!_quoteSubs.Contains(symbol)) _quoteSubs.Add(symbol);
        if (_inner != null) await _inner.SubscribeQuotesAsync(symbol, ct);
    }

    public async Task UnsubscribeQuotesAsync(string symbol, CancellationToken ct = default)
    {
        _quoteSubs.Remove(symbol);
        if (_inner != null) await _inner.UnsubscribeQuotesAsync(symbol, ct);
    }

    public async Task SubscribeDepthAsync(string symbol, CancellationToken ct = default)
    {
        if (!_depthSubs.Contains(symbol)) _depthSubs.Add(symbol);
        if (_inner != null) await _inner.SubscribeDepthAsync(symbol, ct);
    }

    public async Task UnsubscribeDepthAsync(string symbol, CancellationToken ct = default)
    {
        _depthSubs.Remove(symbol);
        if (_inner != null) await _inner.UnsubscribeDepthAsync(symbol, ct);
    }

    public async ValueTask DisposeAsync()
    {
        _quoteSub?.Dispose();
        _depthSub?.Dispose();
        _tradeSub?.Dispose();
        _accountActivitySub?.Dispose();
        _connSub?.Dispose();
        _quoteSubject.Dispose();
        _depthSubject.Dispose();
        _tradeSubject.Dispose();
        _accountActivitySubject.Dispose();
        _connectionSubject.Dispose();
        if (_inner != null)
            await _inner.DisposeAsync();
    }
}
