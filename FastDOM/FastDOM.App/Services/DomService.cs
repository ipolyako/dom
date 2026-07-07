using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using FastDOM.Core.Models;
using FastDOM.MarketData.Interfaces;
using FastDOM.MarketData.Models;
using Microsoft.Extensions.Logging;

namespace FastDOM.App.Services;

/// <summary>
/// Maintains the price ladder state for the DOM view.
/// Combines market data (L1/L2) with working orders and position info.
/// </summary>
public class DomService : IDisposable
{
    private readonly ILogger<DomService> _logger;
    private readonly IMarketDataClient _marketData;

    private Quote? _currentQuote;
    private MarketDepth? _currentDepth;
    private SymbolInfo _symbolInfo = SymbolInfo.Default("SPY");
    private readonly List<IDisposable> _subscriptions = [];

    public event Action? DomUpdated;
    public event Action<Quote>? QuoteUpdated;

    public Quote? CurrentQuote => _currentQuote;
    public MarketDepth? CurrentDepth => _currentDepth;
    public SymbolInfo SymbolInfo => _symbolInfo;

    public DomService(ILogger<DomService> logger, IMarketDataClient marketData)
    {
        _logger = logger;
        _marketData = marketData;

        _subscriptions.Add(marketData.QuoteStream.Subscribe(OnQuote));
        _subscriptions.Add(marketData.DepthStream.Subscribe(OnDepth));
    }

    public async Task SubscribeAsync(string symbol)
    {
        _symbolInfo = SymbolInfo.Default(symbol);
        await _marketData.SubscribeQuotesAsync(symbol);
        await _marketData.SubscribeDepthAsync(symbol);

        var snapshot = await _marketData.GetSnapshotAsync(symbol);
        if (snapshot != null) OnQuote(snapshot);
    }

    public async Task UnsubscribeAsync(string symbol)
    {
        await _marketData.UnsubscribeQuotesAsync(symbol);
        await _marketData.UnsubscribeDepthAsync(symbol);
    }

    public List<DomLadderRow> BuildLadder(
        int visibleLevels,
        IEnumerable<OrderState> workingOrders,
        Position? position)
    {
        if (_currentQuote == null) return [];

        decimal center = _currentQuote.Last;
        decimal tick = _symbolInfo.TickSize;
        int halfLevels = visibleLevels / 2;

        var rows = new List<DomLadderRow>(visibleLevels);

        // Build depth lookup
        var bidSizes = new Dictionary<decimal, int>();
        var askSizes = new Dictionary<decimal, int>();
        if (_currentDepth != null)
        {
            foreach (var b in _currentDepth.Bids) bidSizes[b.Price] = b.BidSize;
            foreach (var a in _currentDepth.Asks) askSizes[a.Price] = a.AskSize;
        }

        // Working order lookup
        var buyOrders = workingOrders
            .Where(o => o.IsWorking && o.Side == Core.Enums.OrderSide.Buy && o.LimitPrice.HasValue)
            .GroupBy(o => _symbolInfo.RoundToTick(o.LimitPrice!.Value))
            .ToDictionary(g => g.Key, g => g.ToList());

        var sellOrders = workingOrders
            .Where(o => o.IsWorking && o.Side == Core.Enums.OrderSide.Sell && o.LimitPrice.HasValue)
            .GroupBy(o => _symbolInfo.RoundToTick(o.LimitPrice!.Value))
            .ToDictionary(g => g.Key, g => g.ToList());

        decimal positionPrice = position?.AverageCost ?? 0;

        for (int i = halfLevels; i >= -halfLevels; i--)
        {
            decimal price = _symbolInfo.RoundToTick(center + i * tick);
            bool isBid = price == _symbolInfo.RoundToTick(_currentQuote.Bid);
            bool isAsk = price == _symbolInfo.RoundToTick(_currentQuote.Ask);
            bool isLast = price == _symbolInfo.RoundToTick(_currentQuote.Last);
            bool isPosition = position != null && !position.IsFlat &&
                              price == _symbolInfo.RoundToTick(positionPrice);

            int bidSize = bidSizes.GetValueOrDefault(price, isBid ? _currentQuote.BidSize : 0);
            int askSize = askSizes.GetValueOrDefault(price, isAsk ? _currentQuote.AskSize : 0);

            var row = new DomLadderRow
            {
                Price        = price,
                BidSize      = bidSize,
                AskSize      = askSize,
                IsBid        = isBid,
                IsAsk        = isAsk,
                IsLast       = isLast,
                IsPosition   = isPosition,
                HasRealDepth = _currentDepth?.HasRealDepth ?? false
            };
            foreach (var o in buyOrders.GetValueOrDefault(price, []))  row.BuyOrders.Add(o);
            foreach (var o in sellOrders.GetValueOrDefault(price, [])) row.SellOrders.Add(o);
            rows.Add(row);
        }

        return rows;
    }

    private void OnQuote(Quote q)
    {
        if (q.Symbol != _symbolInfo.Symbol) return;
        _currentQuote = q;
        QuoteUpdated?.Invoke(q);
        DomUpdated?.Invoke();
    }

    private void OnDepth(MarketDepth d)
    {
        if (d.Symbol != _symbolInfo.Symbol) return;
        _currentDepth = d;
        DomUpdated?.Invoke();
    }

    public void Dispose()
    {
        foreach (var s in _subscriptions) s.Dispose();
    }
}

public partial class DomLadderRow : ObservableObject
{
    [ObservableProperty] private decimal _price;
    [ObservableProperty] private int _bidSize;
    [ObservableProperty] private int _askSize;
    [ObservableProperty] private bool _isBid;
    [ObservableProperty] private bool _isAsk;
    [ObservableProperty] private bool _isLast;
    [ObservableProperty] private bool _isPosition;
    [ObservableProperty] private bool _hasRealDepth;

    public ObservableCollection<OrderState> BuyOrders { get; } = [];
    public ObservableCollection<OrderState> SellOrders { get; } = [];

    public bool HasBuyOrders => BuyOrders.Count > 0;
    public bool HasSellOrders => SellOrders.Count > 0;

    public string BuyOrderSummary => BuyOrders.Count > 0
        ? string.Join("+", BuyOrders.Select(o => o.QuantityRemaining))
        : "";

    public string SellOrderSummary => SellOrders.Count > 0
        ? string.Join("+", SellOrders.Select(o => o.QuantityRemaining))
        : "";

    public DomLadderRow()
    {
        BuyOrders.CollectionChanged  += (_, _) => { OnPropertyChanged(nameof(BuyOrderSummary));  OnPropertyChanged(nameof(HasBuyOrders));  };
        SellOrders.CollectionChanged += (_, _) => { OnPropertyChanged(nameof(SellOrderSummary)); OnPropertyChanged(nameof(HasSellOrders)); };
    }
}
