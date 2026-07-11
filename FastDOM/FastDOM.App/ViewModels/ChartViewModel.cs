using CommunityToolkit.Mvvm.ComponentModel;
using FastDOM.App.Services;
using FastDOM.MarketData.Interfaces;
using FastDOM.MarketData.Models;

namespace FastDOM.App.ViewModels;

public record ChartTimeframe(string Label, PriceHistoryRequest Request, int AggregateMinutes = 0)
{
    public override string ToString() => Label;
}

public partial class ChartViewModel : ObservableObject, IDisposable
{
    private readonly IPriceHistoryClient _history;
    private readonly IMarketDataClient _marketData;
    private readonly IDisposable _quoteSubscription;
    private readonly IDisposable _depthSubscription;
    private CancellationTokenSource? _loadCts;
    private string? _subscribedSymbol;

    [ObservableProperty] private string _symbol = "SPY";
    [ObservableProperty] private ChartTimeframe _selectedTimeframe;
    [ObservableProperty] private string _status = "Ready";
    [ObservableProperty] private bool _includeExtendedHours = true;
    public IReadOnlyList<PriceCandle> Candles { get; private set; } = [];
    public MarketDepth? CurrentDepth { get; private set; }
    public event Action? ChartChanged;

    public IReadOnlyList<ChartTimeframe> Timeframes { get; } =
    [
        new("1m · 1D", new("day", 1, "minute", 1)),
        new("5m · 5D", new("day", 5, "minute", 5)),
        new("15m · 10D", new("day", 10, "minute", 15)),
        new("30m · 10D", new("day", 10, "minute", 30)),
        new("1H · 10D", new("day", 10, "minute", 30), 60),
        new("4H · 10D", new("day", 10, "minute", 30), 240),
        new("1D · 1M", new("month", 1, "daily", 1)),
        new("1D · 3M", new("month", 3, "daily", 1)),
        new("1D · 1Y", new("year", 1, "daily", 1)),
        new("1D · 5Y", new("year", 5, "daily", 1))
    ];

    public ChartViewModel(IPriceHistoryClient history, IMarketDataClient marketData)
    {
        _history = history;
        _marketData = marketData;
        _selectedTimeframe = Timeframes[1];
        _quoteSubscription = marketData.QuoteStream.Subscribe(OnQuote);
        _depthSubscription = marketData.DepthStream.Subscribe(OnDepth);
    }

    public async Task LoadAsync(string? symbol = null)
    {
        if (!string.IsNullOrWhiteSpace(symbol)) Symbol = SymbolClassifier.NormalizeDisplaySymbol(symbol);
        _loadCts?.Cancel();
        _loadCts?.Dispose();
        _loadCts = new CancellationTokenSource();
        Status = $"Loading {Symbol} {SelectedTimeframe.Label}…";
        try
        {
            if (!string.Equals(_subscribedSymbol, Symbol, StringComparison.OrdinalIgnoreCase))
            {
                if (_subscribedSymbol != null)
                {
                    await _marketData.UnsubscribeQuotesAsync(_subscribedSymbol, _loadCts.Token);
                    await _marketData.UnsubscribeDepthAsync(_subscribedSymbol, _loadCts.Token);
                }
                CurrentDepth = null;
                await _marketData.SubscribeQuotesAsync(Symbol, _loadCts.Token);
                await _marketData.SubscribeDepthAsync(Symbol, _loadCts.Token);
                _subscribedSymbol = Symbol;
            }
            var request = SelectedTimeframe.Request with { IncludeExtendedHours = IncludeExtendedHours };
            var candles = await _history.GetPriceHistoryAsync(Symbol, request, _loadCts.Token);
            Candles = SelectedTimeframe.AggregateMinutes > 0
                ? Aggregate(candles, SelectedTimeframe.AggregateMinutes)
                : candles;
            Status = Candles.Count == 0 ? $"No history returned for {Symbol}" :
                $"{Symbol} · {SelectedTimeframe.Label} · {Candles.Count:N0} candles · {Candles[^1].Timestamp:g}";
            ChartChanged?.Invoke();
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { Status = ex.Message; }
    }

    private static IReadOnlyList<PriceCandle> Aggregate(IReadOnlyList<PriceCandle> source, int minutes)
    {
        if (source.Count == 0 || minutes <= 0) return source;
        return source
            .GroupBy(c =>
            {
                var bucket = (c.Timestamp.Hour * 60 + c.Timestamp.Minute) / minutes * minutes;
                return new DateTime(c.Timestamp.Year, c.Timestamp.Month, c.Timestamp.Day,
                    bucket / 60, bucket % 60, 0, c.Timestamp.Kind);
            })
            .OrderBy(group => group.Key)
            .Select(group =>
            {
                var ordered = group.OrderBy(c => c.Timestamp).ToArray();
                return new PriceCandle
                {
                    Timestamp = group.Key,
                    Open = ordered[0].Open,
                    High = ordered.Max(c => c.High),
                    Low = ordered.Min(c => c.Low),
                    Close = ordered[^1].Close,
                    Volume = ordered.Sum(c => c.Volume)
                };
            })
            .ToArray();
    }

    private void OnQuote(Quote quote)
    {
        if (!string.Equals(quote.Symbol, Symbol, StringComparison.OrdinalIgnoreCase) || Candles.Count == 0 || quote.Last <= 0) return;
        var list = Candles.ToList();
        var last = list[^1];
        last.Close = quote.Last;
        last.High = Math.Max(last.High, quote.Last);
        last.Low = last.Low <= 0 ? quote.Last : Math.Min(last.Low, quote.Last);
        if (quote.Volume > 0) last.Volume = quote.Volume;
        Candles = list;
        ChartChanged?.Invoke();
    }

    private void OnDepth(MarketDepth depth)
    {
        if (!string.Equals(depth.Symbol, Symbol, StringComparison.OrdinalIgnoreCase)) return;
        CurrentDepth = depth;
        ChartChanged?.Invoke();
    }

    public void Dispose()
    {
        _loadCts?.Cancel();
        _loadCts?.Dispose();
        _quoteSubscription.Dispose();
        _depthSubscription.Dispose();
        if (_subscribedSymbol != null)
        {
            _ = _marketData.UnsubscribeQuotesAsync(_subscribedSymbol);
            _ = _marketData.UnsubscribeDepthAsync(_subscribedSymbol);
        }
    }
}
