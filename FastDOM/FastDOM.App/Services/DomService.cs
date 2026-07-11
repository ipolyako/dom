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
    private decimal? _ladderCenterPrice;
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
        _ladderCenterPrice = null;
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
        int priceStepTicks,
        IEnumerable<OrderState> workingOrders,
        Position? position)
    {
        if (_currentQuote == null) { target.Clear(); return; }

        _bidSizesScratch.Clear();
        _askSizesScratch.Clear();
        _buyOrdersScratch.Clear();
        _sellOrdersScratch.Clear();

        decimal lastPrice = _symbolInfo.RoundToTick(_currentQuote.Last);
        decimal tick   = _symbolInfo.TickSize;
        decimal step   = tick * Math.Max(1, priceStepTicks);
        decimal center = BucketPrice(_ladderCenterPrice ?? lastPrice, step);
        _ladderCenterPrice = center;
        int totalLevels = Math.Clamp(visibleLevels, 20, 400);
        int levelsAbove = totalLevels / 2;
        int levelsBelow = totalLevels - levelsAbove - 1;
        bool hasRealDepth = _currentDepth?.HasRealDepth ?? false;

        if (_currentDepth != null)
        {
            foreach (var b in _currentDepth.Bids) AddSizeAt(_bidSizesScratch, BucketPrice(b.Price, step), b.BidSize);
            foreach (var a in _currentDepth.Asks) AddSizeAt(_askSizesScratch, BucketPrice(a.Price, step), a.AskSize);
        }

        // Market orders pin to best quote (ask for buys, bid for sells).
        decimal mktBuyPrice  = BucketPrice(_currentQuote.Ask > 0 ? _currentQuote.Ask : center, step);
        decimal mktSellPrice = BucketPrice(_currentQuote.Bid > 0 ? _currentQuote.Bid : center, step);

        foreach (var o in workingOrders)
        {
            if (!o.IsWorking) continue;
            var side = o.Side;
            var dict = side == Core.Enums.OrderSide.Buy ? _buyOrdersScratch : _sellOrdersScratch;
            if (o.LimitPrice.HasValue)
                AddOrderAt(dict, BucketPrice(o.LimitPrice.Value, step), o);
            if (o.StopPrice.HasValue)
                AddOrderAt(dict, BucketPrice(o.StopPrice.Value, step), o);
            if (!o.LimitPrice.HasValue && !o.StopPrice.HasValue)
                AddOrderAt(dict, side == Core.Enums.OrderSide.Buy ? mktBuyPrice : mktSellPrice, o);
        }

        decimal bidPrice = BucketPrice(_currentQuote.Bid, step);
        decimal askPrice = BucketPrice(_currentQuote.Ask, step);
        lastPrice = BucketPrice(lastPrice, step);
        decimal positionPrice = position?.AverageCost ?? 0;
        decimal positionRounded = position != null && !position.IsFlat ? BucketPrice(positionPrice, step) : 0m;
        bool hasPosition = position != null && !position.IsFlat;
        int positionQty = hasPosition ? position!.Quantity : 0;

        int rowIndex = 0;
        for (int i = levelsAbove; i >= -levelsBelow; i--)
        {
            decimal price = BucketPrice(center + i * step, step);
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
            row.PriceLevelPnL = hasPosition ? (price - positionPrice) * positionQty : null;
            row.PriceLevelPnLDisplay = hasPosition ? FormatPnL(row.PriceLevelPnL!.Value) : "";

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

    public void CenterLadderOnLast()
    {
        if (_currentQuote == null) return;
        _ladderCenterPrice = _symbolInfo.RoundToTick(_currentQuote.Last);
    }

    public bool CenterLadderOnLastIfOutsideVisibleRange(int visibleLevels, int priceStepTicks)
    {
        if (_currentQuote == null) return false;

        decimal tick = _symbolInfo.TickSize;
        decimal step = tick * Math.Max(1, priceStepTicks);
        decimal lastPrice = BucketPrice(_symbolInfo.RoundToTick(_currentQuote.Last), step);
        decimal center = BucketPrice(_ladderCenterPrice ?? lastPrice, step);

        int totalLevels = Math.Clamp(visibleLevels, 20, 400);
        int levelsAbove = totalLevels / 2;
        int levelsBelow = totalLevels - levelsAbove - 1;
        decimal top = BucketPrice(center + levelsAbove * step, step);
        decimal bottom = BucketPrice(center - levelsBelow * step, step);

        if (lastPrice <= top && lastPrice >= bottom)
            return false;

        _ladderCenterPrice = lastPrice;
        return true;
    }

    private static void AddOrderAt(Dictionary<decimal, List<OrderState>> dict, decimal price, OrderState order)
    {
        if (!dict.TryGetValue(price, out var list)) { list = new List<OrderState>(2); dict[price] = list; }
        list.Add(order);
    }

    private static void AddSizeAt(Dictionary<decimal, int> dict, decimal price, int size)
    {
        if (size <= 0) return;
        dict[price] = dict.GetValueOrDefault(price) + size;
    }

    public static decimal BucketPrice(decimal price, decimal step)
    {
        if (step <= 0) return price;
        return Math.Round(price / step, 0, MidpointRounding.AwayFromZero) * step;
    }

    private static string FormatPnL(decimal value) =>
        value >= 0 ? $"+${value:F2}" : $"-${Math.Abs(value):F2}";

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
    [ObservableProperty] private decimal? _priceLevelPnL;
    [ObservableProperty] private string _priceLevelPnLDisplay = "";

    public ObservableCollection<OrderState> BuyOrders { get; } = [];
    public ObservableCollection<OrderState> SellOrders { get; } = [];

    public bool HasBuyOrders => BuyOrders.Count > 0;
    public bool HasSellOrders => SellOrders.Count > 0;

    public string BuyOrderSummary => BuyOrders.Count > 0
        ? FormatOrderSummary(BuyOrders)
        : "";

    public string SellOrderSummary => SellOrders.Count > 0
        ? FormatOrderSummary(SellOrders)
        : "";

    private static string FormatOrderSummary(IReadOnlyCollection<OrderState> orders)
    {
        var total = orders.Sum(order => Math.Max(0, order.QuantityRemaining));
        return orders.Count == 1 ? total.ToString("N0") : $"{total:N0} ×{orders.Count}";
    }

    public DomLadderRow()
    {
        BuyOrders.CollectionChanged  += (_, _) => { OnPropertyChanged(nameof(BuyOrderSummary));  OnPropertyChanged(nameof(HasBuyOrders));  };
        SellOrders.CollectionChanged += (_, _) => { OnPropertyChanged(nameof(SellOrderSummary)); OnPropertyChanged(nameof(HasSellOrders)); };
    }
}
