using System.Collections.Concurrent;
using FastDOM.Core.Models;
using FastDOM.MarketData.Interfaces;
using FastDOM.MarketData.Models;
using Microsoft.Extensions.Logging;

namespace FastDOM.App.Services;

public enum FootprintTradeSide { Bid, Ask, Unknown }

public sealed record FootprintLevel(
    DateTime BarTimeUtc, decimal Price, long BidVolume, long AskVolume,
    long UnknownVolume, int TradeCount)
{
    public long TotalVolume => BidVolume + AskVolume + UnknownVolume;
    public long Delta => AskVolume - BidVolume;
}

public sealed class FootprintService : IDisposable
{
    private readonly ILogger<FootprintService> _logger;
    private readonly FootprintDerbyRepository _repository;
    private readonly IDisposable _quoteSubscription;
    private readonly IDisposable _tradeSubscription;
    private readonly ConcurrentDictionary<string, Quote> _quotes = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, decimal> _lastTrades = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<FootprintKey, MutableLevel> _levels = new();
    private ConcurrentDictionary<FootprintKey, MutableLevel> _dirty = new();
    private readonly ConcurrentDictionary<string, byte> _historyLoaded = new(StringComparer.OrdinalIgnoreCase);
    private readonly CancellationTokenSource _cts = new();
    private readonly Task _flushLoop;

    public FootprintService(ILogger<FootprintService> logger,
        IMarketDataClient marketData, FootprintDerbyRepository repository)
    {
        _logger = logger;
        _repository = repository;
        _quoteSubscription = marketData.QuoteStream.Subscribe(q => _quotes[q.Symbol] = CopyQuote(q));
        _tradeSubscription = marketData.TradeStream.Subscribe(OnTrade);
        _flushLoop = Task.Run(() => FlushLoopAsync(_cts.Token));
    }

    public async Task LoadHistoryAsync(string symbol, CancellationToken ct = default)
    {
        symbol = symbol.Trim().ToUpperInvariant();
        if (!_historyLoaded.TryAdd(symbol, 0)) return;
        try
        {
            var rows = await _repository.LoadAsync(symbol, 10_000, ct);
            foreach (var row in rows)
            {
                var key = new FootprintKey(symbol, row.BarTimeUtc, row.Price);
                _levels.TryAdd(key, new MutableLevel(row.BidVolume, row.AskVolume,
                    row.UnknownVolume, row.TradeCount));
            }
        }
        catch (Exception ex)
        {
            _historyLoaded.TryRemove(symbol, out _);
            _logger.LogWarning(ex, "Footprint history load failed for {Symbol}", symbol);
        }
    }

    public IReadOnlyList<FootprintLevel> Snapshot(string symbol, int timeframeMinutes = 1, int maxBars = 18)
    {
        symbol = symbol.Trim().ToUpperInvariant();
        timeframeMinutes = Math.Max(1, timeframeMinutes);
        var grouped = _levels
            .Where(x => x.Key.Symbol.Equals(symbol, StringComparison.OrdinalIgnoreCase))
            .Select(x => new FootprintLevel(x.Key.BarTimeUtc, x.Key.Price,
                x.Value.BidVolume, x.Value.AskVolume, x.Value.UnknownVolume, x.Value.TradeCount))
            .GroupBy(x => new { Bar = BucketTime(x.BarTimeUtc, timeframeMinutes), x.Price })
            .Select(g => new FootprintLevel(g.Key.Bar, g.Key.Price,
                g.Sum(x => x.BidVolume), g.Sum(x => x.AskVolume),
                g.Sum(x => x.UnknownVolume), g.Sum(x => x.TradeCount)))
            .ToArray();
        var bars = grouped.Select(x => x.BarTimeUtc).Distinct().OrderByDescending(x => x)
            .Take(maxBars).ToHashSet();
        return grouped.Where(x => bars.Contains(x.BarTimeUtc))
            .OrderBy(x => x.BarTimeUtc).ThenByDescending(x => x.Price).ToArray();
    }

    private void OnTrade(Trade trade)
    {
        if (trade.Price <= 0 || trade.Size <= 0) return;
        var symbol = trade.Symbol.Trim().ToUpperInvariant();
        var tick = SymbolInfo.Default(symbol).TickSize;
        var price = tick > 0 ? Math.Round(trade.Price / tick, MidpointRounding.AwayFromZero) * tick : trade.Price;
        var bar = BucketTime(trade.TimestampUtc == default ? DateTime.UtcNow : trade.TimestampUtc, 1);
        var side = Classify(symbol, price);
        var key = new FootprintKey(symbol, bar, price);
        Add(_levels.GetOrAdd(key, _ => new MutableLevel()), side, trade.Size);
        Add(_dirty.GetOrAdd(key, _ => new MutableLevel()), side, trade.Size);
        _lastTrades[symbol] = price;
    }

    private FootprintTradeSide Classify(string symbol, decimal price)
    {
        if (_quotes.TryGetValue(symbol, out var quote))
        {
            if (quote.Ask > 0 && price >= quote.Ask) return FootprintTradeSide.Ask;
            if (quote.Bid > 0 && price <= quote.Bid) return FootprintTradeSide.Bid;
        }
        if (_lastTrades.TryGetValue(symbol, out var prior))
        {
            if (price > prior) return FootprintTradeSide.Ask;
            if (price < prior) return FootprintTradeSide.Bid;
        }
        return FootprintTradeSide.Unknown;
    }

    private static void Add(MutableLevel level, FootprintTradeSide side, int size)
    {
        lock (level)
        {
            if (side == FootprintTradeSide.Bid) level.BidVolume += size;
            else if (side == FootprintTradeSide.Ask) level.AskVolume += size;
            else level.UnknownVolume += size;
            level.TradeCount++;
        }
    }

    private async Task FlushLoopAsync(CancellationToken ct)
    {
        // Derby/JVM work is intentionally low-frequency and off the execution path.
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(30));
        try
        {
            while (await timer.WaitForNextTickAsync(ct)) await FlushAsync(ct);
        }
        catch (OperationCanceledException) { }
    }

    private async Task FlushAsync(CancellationToken ct)
    {
        var batch = Interlocked.Exchange(ref _dirty, new ConcurrentDictionary<FootprintKey, MutableLevel>());
        if (batch.IsEmpty) return;
        var rows = batch.Select(x => new FootprintPersistRow(x.Key.Symbol, x.Key.BarTimeUtc, x.Key.Price,
            x.Value.BidVolume, x.Value.AskVolume, x.Value.UnknownVolume, x.Value.TradeCount)).ToArray();
        try { await _repository.UpsertAsync(rows, ct); }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Footprint Derby flush failed; retaining {Count} rows in memory", rows.Length);
            foreach (var item in batch)
            {
                var target = _dirty.GetOrAdd(item.Key, _ => new MutableLevel());
                lock (target)
                {
                    target.BidVolume += item.Value.BidVolume;
                    target.AskVolume += item.Value.AskVolume;
                    target.UnknownVolume += item.Value.UnknownVolume;
                    target.TradeCount += item.Value.TradeCount;
                }
            }
        }
    }

    private static DateTime BucketTime(DateTime utc, int minutes)
    {
        utc = utc.Kind == DateTimeKind.Utc ? utc : utc.ToUniversalTime();
        return new DateTime(utc.Year, utc.Month, utc.Day, utc.Hour,
            utc.Minute - utc.Minute % minutes, 0, DateTimeKind.Utc);
    }

    private static Quote CopyQuote(Quote q) => new()
    {
        Symbol = q.Symbol, Bid = q.Bid, Ask = q.Ask, Last = q.Last, TimestampUtc = q.TimestampUtc
    };

    public void Dispose()
    {
        _quoteSubscription.Dispose();
        _tradeSubscription.Dispose();
        _cts.Cancel();
        try { FlushAsync(CancellationToken.None).GetAwaiter().GetResult(); } catch { }
        _cts.Dispose();
    }

    private readonly record struct FootprintKey(string Symbol, DateTime BarTimeUtc, decimal Price);
    private sealed class MutableLevel
    {
        public MutableLevel() { }
        public MutableLevel(long bid, long ask, long unknown, int count)
            => (BidVolume, AskVolume, UnknownVolume, TradeCount) = (bid, ask, unknown, count);
        public long BidVolume;
        public long AskVolume;
        public long UnknownVolume;
        public int TradeCount;
    }
}
