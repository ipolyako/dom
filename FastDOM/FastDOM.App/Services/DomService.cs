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
        // Unsubscribe previous symbol so its timer stops before the new one starts
        if (!string.IsNullOrEmpty(_symbolInfo.Symbol) && _symbolInfo.Symbol != symbol)
            await UnsubscribeAsync(_symbolInfo.Symbol);

        _currentQuote = null;
        _currentDepth = null;
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

    // Scratch dictionaries reused every tick to avoid allocations.
    private readonly Dictionary<decimal, int> _bidSizesScratch = new();
    private readonly Dictionary<decimal, int> _askSizesScratch = new();
    private readonly Dictionary<decimal, List<OrderState>> _buyOrdersScratch = new();
    private readonly Dictionary<decimal, List<OrderState>> _sellOrdersScratch = new();
    private static readonly List<OrderState> _emptyOrders = new();

    // Mutates target in place: no per-tick List<DomLadderRow> / DomLadderRow /
    // Dictionary allocations, no LINQ. Existing rows are updated; extra rows
    // appended or removed at the tail only.
    public void PopulateLadder(
        ObservableCollection<DomLadderRow> target,
        int visibleLevels,
        IEnumerable<OrderState> workingOrders,
        Position? position)
    {
        if (_currentQuote == null) { target.Clear(); return; }

        _bidSizesScratch.Clear();
        _askSizesScratch.Clear();
        _buyOrdersScratch.Clear();
        _sellOrdersScratch.Clear();

        if (_currentDepth != null)
        {
            foreach (var b in _currentDepth.Bids) _bidSizesScratch[b.Price] = b.BidSize;
            foreach (var a in _currentDepth.Asks) _askSizesScratch[a.Price] = a.AskSize;
        }

        decimal center = _currentQuote.Last;
        decimal tick   = _symbolInfo.TickSize;
        int halfLevels = visibleLevels / 2;
        bool hasRealDepth = _currentDepth?.HasRealDepth ?? false;

        // Market orders pin to best quote (ask for buys, bid for sells).
        decimal mktBuyPrice  = _symbolInfo.RoundToTick(_currentQuote.Ask > 0 ? _currentQuote.Ask : center);
        decimal mktSellPrice = _symbolInfo.RoundToTick(_currentQuote.Bid > 0 ? _currentQuote.Bid : center);

        foreach (var o in workingOrders)
        {
            if (!o.IsWorking) continue;
            var side = o.Side;
            var dict = side == Core.Enums.OrderSide.Buy ? _buyOrdersScratch : _sellOrdersScratch;
            var key  = o.LimitPrice.HasValue
                       ? _symbolInfo.RoundToTick(o.LimitPrice.Value)
                       : (side == Core.Enums.OrderSide.Buy ? mktBuyPrice : mktSellPrice);
            if (!dict.TryGetValue(key, out var list)) { list = new List<OrderState>(2); dict[key] = list; }
            list.Add(o);
        }

        decimal bidPrice = _symbolInfo.RoundToTick(_currentQuote.Bid);
        decimal askPrice = _symbolInfo.RoundToTick(_currentQuote.Ask);
        decimal lastPrice = _symbolInfo.RoundToTick(_currentQuote.Last);
        decimal positionPrice = position?.AverageCost ?? 0;
        decimal positionRounded = position != null && !position.IsFlat ? _symbolInfo.RoundToTick(positionPrice) : 0m;
        bool hasPosition = position != null && !position.IsFlat;

        int rowIndex = 0;
        for (int i = halfLevels; i >= -halfLevels; i--)
        {
            decimal price = _symbolInfo.RoundToTick(center + i * tick);
            bool isBid = price == bidPrice;
            bool isAsk = price == askPrice;
            bool isLast = price == lastPrice;
            bool isPosition = hasPosition && price == positionRounded;

            int bidSize = _bidSizesScratch.GetValueOrDefault(price, isBid ? _currentQuote.BidSize : 0);
            int askSize = _askSizesScratch.GetValueOrDefault(price, isAsk ? _currentQuote.AskSize : 0);

            DomLadderRow row;
            if (rowIndex < target.Count) row = target[rowIndex];
            else { row = new DomLadderRow(); target.Add(row); }

            row.Price        = price;
            row.BidSize      = bidSize;
            row.AskSize      = askSize;
            row.IsBid        = isBid;
            row.IsAsk        = isAsk;
            row.IsLast       = isLast;
            row.IsPosition   = isPosition;
            row.HasRealDepth = hasRealDepth;

            SyncOrderList(_buyOrdersScratch.GetValueOrDefault(price, _emptyOrders),  row.BuyOrders);
            SyncOrderList(_sellOrdersScratch.GetValueOrDefault(price, _emptyOrders), row.SellOrders);

            rowIndex++;
        }

        while (target.Count > rowIndex) target.RemoveAt(target.Count - 1);
    }

    private static void SyncOrderList(IReadOnlyList<OrderState> src, ObservableCollection<OrderState> dst)
    {
        if (src.Count == dst.Count)
        {
            bool same = true;
            for (int i = 0; i < src.Count; i++)
            {
                if (!ReferenceEquals(src[i], dst[i])) { same = false; break; }
            }
            if (same) return;
        }
        dst.Clear();
        for (int i = 0; i < src.Count; i++) dst.Add(src[i]);
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
