using System.Reactive.Linq;
using System.Reactive.Subjects;
using FastDOM.MarketData.Interfaces;
using FastDOM.MarketData.Models;
using Microsoft.Extensions.Logging;

namespace FastDOM.MarketData.Mock;

public class MockMarketDataClient : IMarketDataClient
{
    private readonly ILogger<MockMarketDataClient> _logger;
    private readonly Subject<Quote> _quoteSubject = new();
    private readonly Subject<MarketDepth> _depthSubject = new();
    private readonly Subject<Trade> _tradeSubject = new();
    private readonly Subject<bool> _connectionSubject = new();
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, Quote> _quotes = new();
    private readonly Dictionary<string, Timer> _timers = [];
    // Random.Shared is thread-safe in .NET 6+; multiple timer callbacks would corrupt a per-instance Random
    private static readonly Random _rng = Random.Shared;
    private bool _connected;

    public bool IsConnected => _connected;
    public bool SupportsLevelTwo => true;
    public IObservable<Quote> QuoteStream => _quoteSubject.AsObservable();
    public IObservable<MarketDepth> DepthStream => _depthSubject.AsObservable();
    public IObservable<Trade> TradeStream => _tradeSubject.AsObservable();
    public IObservable<bool> ConnectionStateStream => _connectionSubject.AsObservable();

    public MockMarketDataClient(ILogger<MockMarketDataClient> logger)
    {
        _logger = logger;
    }

    public Task ConnectAsync(CancellationToken ct = default)
    {
        _connected = true;
        _connectionSubject.OnNext(true);
        _logger.LogInformation("MockMarketDataClient connected");
        return Task.CompletedTask;
    }

    public Task DisconnectAsync(CancellationToken ct = default)
    {
        _connected = false;
        foreach (var t in _timers.Values) t.Dispose();
        _timers.Clear();
        _connectionSubject.OnNext(false);
        return Task.CompletedTask;
    }

    public Task<Quote?> GetSnapshotAsync(string symbol, CancellationToken ct = default)
    {
        var q = BuildInitialQuote(symbol);
        return Task.FromResult<Quote?>(q);
    }

    public Task SubscribeQuotesAsync(string symbol, CancellationToken ct = default)
    {
        symbol = symbol.ToUpperInvariant();
        if (_timers.ContainsKey(symbol)) return Task.CompletedTask;

        var q = BuildInitialQuote(symbol);
        _quotes[symbol] = q;
        _quoteSubject.OnNext(q);

        var timer = new Timer(_ => TickSymbol(symbol), null, 250, 200 + _rng.Next(0, 300));
        _timers[symbol] = timer;
        _logger.LogInformation("MockMarketData subscribed to {Symbol}", symbol);
        return Task.CompletedTask;
    }

    public Task UnsubscribeQuotesAsync(string symbol, CancellationToken ct = default)
    {
        symbol = symbol.ToUpperInvariant();
        if (_timers.TryGetValue(symbol, out var t)) { t.Dispose(); _timers.Remove(symbol); }
        return Task.CompletedTask;
    }

    public Task SubscribeDepthAsync(string symbol, CancellationToken ct = default)
    {
        symbol = symbol.ToUpperInvariant();
        PublishMockDepth(symbol);
        return Task.CompletedTask;
    }

    public Task UnsubscribeDepthAsync(string symbol, CancellationToken ct = default) => Task.CompletedTask;

    private Quote BuildInitialQuote(string symbol)
    {
        // Seed realistic-ish prices per well-known symbol
        decimal seed = symbol switch
        {
            "SPY"  => 550.00m,
            "QQQ"  => 480.00m,
            "NVDA" => 135.00m,
            "TSLA" => 260.00m,
            "TQQQ" => 70.00m,
            "SQQQ" => 8.00m,
            _      => 100.00m
        };
        decimal ask = seed + 0.01m;
        return new Quote
        {
            Symbol = symbol,
            Last   = seed,
            Bid    = seed - 0.01m,
            Ask    = ask,
            Open   = seed * 0.998m,
            Close  = seed * 0.997m,
            High   = seed * 1.005m,
            Low    = seed * 0.993m,
            Volume = 5_000_000,
            BidSize = 300,
            AskSize = 200,
            NetChange = seed * 0.003m,
        };
    }

    private void TickSymbol(string symbol)
    {
        if (!_quotes.TryGetValue(symbol, out var prev)) return;

        decimal move = (decimal)(_rng.NextDouble() - 0.499) * 0.04m;
        decimal last = Math.Max(0.01m, prev.Last + move);
        last = Math.Round(last, 2);

        var q = new Quote
        {
            Symbol    = symbol,
            Last      = last,
            Bid       = last - 0.01m,
            Ask       = last + 0.01m,
            Open      = prev.Open,
            Close     = prev.Close,
            High      = Math.Max(prev.High, last),
            Low       = Math.Min(prev.Low, last),
            Volume    = prev.Volume + _rng.Next(100, 2000),
            BidSize   = _rng.Next(100, 1000),
            AskSize   = _rng.Next(100, 1000),
            NetChange = last - prev.Close,
            NetChangePct = prev.Close > 0 ? (last - prev.Close) / prev.Close * 100m : 0m,
        };

        _quotes[symbol] = q;
        _quoteSubject.OnNext(q);

        if (_rng.Next(0, 5) == 0)
            _tradeSubject.OnNext(new Trade { Symbol = symbol, Price = last, Size = _rng.Next(1, 500) });

        if (_rng.Next(0, 10) == 0)
            PublishMockDepth(symbol);
    }

    private void PublishMockDepth(string symbol)
    {
        if (!_quotes.TryGetValue(symbol, out var q)) return;
        decimal mid = q.Mid;
        var depth = new MarketDepth { Symbol = symbol, HasRealDepth = false };
        for (int i = 1; i <= 10; i++)
        {
            depth.Bids.Add(new DomLevel { Price = mid - i * 0.01m, BidSize = _rng.Next(100, 2000) });
            depth.Asks.Add(new DomLevel { Price = mid + i * 0.01m, AskSize = _rng.Next(100, 2000) });
        }
        _depthSubject.OnNext(depth);
    }

    public ValueTask DisposeAsync()
    {
        foreach (var t in _timers.Values) t.Dispose();
        _quoteSubject.Dispose();
        _depthSubject.Dispose();
        _tradeSubject.Dispose();
        _connectionSubject.Dispose();
        return ValueTask.CompletedTask;
    }
}
