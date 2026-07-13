using System.Collections.ObjectModel;
using System.Windows.Media;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using FastDOM.App.Services;
using FastDOM.MarketData.Interfaces;
using FastDOM.MarketData.Models;

namespace FastDOM.App.ViewModels;

public partial class DepthHeatRow : ObservableObject
{
    [ObservableProperty] private decimal _price;
    [ObservableProperty] private string _priceDisplay = "";
    [ObservableProperty] private int _bidSize;
    [ObservableProperty] private int _askSize;
    [ObservableProperty] private int _totalSize;
    [ObservableProperty] private double _bidWidth;
    [ObservableProperty] private double _askWidth;
    [ObservableProperty] private Brush _bidBrush = Brushes.Transparent;
    [ObservableProperty] private Brush _askBrush = Brushes.Transparent;
    [ObservableProperty] private Brush _heatBrush = Brushes.Transparent;
    [ObservableProperty] private Brush _priceBrush = Brushes.White;
    [ObservableProperty] private bool _isMarketPrice;
    [ObservableProperty] private string _concentration = "";

    public string BidDisplay => BidSize > 0 ? BidSize.ToString("N0") : "";
    public string AskDisplay => AskSize > 0 ? AskSize.ToString("N0") : "";
    public string TotalDisplay => TotalSize > 0 ? TotalSize.ToString("N0") : "";

    partial void OnBidSizeChanged(int value) => OnPropertyChanged(nameof(BidDisplay));
    partial void OnAskSizeChanged(int value) => OnPropertyChanged(nameof(AskDisplay));
    partial void OnTotalSizeChanged(int value) => OnPropertyChanged(nameof(TotalDisplay));
}

public partial class DepthMapViewModel : ObservableObject, IDisposable
{
    private static readonly int[] BinTickSteps = [1, 2, 3, 5, 10, 20, 50, 100, 200, 500];
    private readonly DomService _domService;
    private readonly IMarketDataClient _marketData;
    private readonly Dispatcher _dispatcher;
    private readonly bool _isEditable;
    private readonly IDisposable? _quoteSubscription;
    private readonly IDisposable? _depthSubscription;
    private readonly SemaphoreSlim _symbolGate = new(1, 1);
    private Quote? _currentQuote;
    private MarketDepth? _currentDepth;
    private string? _ownedSymbol;
    private bool _disposed;
    private bool _updateQueued;
    private int _binIndex;
    private double _viewportHeight = 650;

    [ObservableProperty] private string _status = "Waiting for L2 depth";
    [ObservableProperty] private string _symbol = "—";
    [ObservableProperty] private string _binLabel = "1 tick / row";
    [ObservableProperty] private string _levelLabel = "80 levels";
    [ObservableProperty] private int _visibleLevels = 80;
    [ObservableProperty] private double _rowHeight = 8;
    [ObservableProperty] private int _largestOrder;
    [ObservableProperty] private int _visibleLiquidity;
    public bool IsEditable => _isEditable;

    public ObservableCollection<DepthHeatRow> Rows { get; } = [];

    public DepthMapViewModel(DomService domService, IMarketDataClient marketData)
        : this(domService, marketData, false)
    {
    }

    private DepthMapViewModel(DomService domService, IMarketDataClient marketData, bool isEditable)
    {
        _domService = domService;
        _marketData = marketData;
        _isEditable = isEditable;
        _dispatcher = Dispatcher.CurrentDispatcher;
        if (isEditable)
        {
            _quoteSubscription = marketData.QuoteStream.Subscribe(OnQuote);
            _depthSubscription = marketData.DepthStream.Subscribe(OnDepth);
            Symbol = "New symbol";
            Status = "Enter a symbol in the tab above";
        }
        else
        {
            _domService.DomUpdated += OnDomUpdated;
        }
        UpdateLabels();
        if (!isEditable) Rebuild();
    }

    public DepthMapViewModel CreateEditableTab() => new(_domService, _marketData, true);

    public async Task SetSymbolAsync(string value)
    {
        if (!_isEditable || _disposed) return;
        var next = SymbolClassifier.NormalizeDisplaySymbol(value);
        if (string.IsNullOrWhiteSpace(next)) return;

        await _symbolGate.WaitAsync();
        try
        {
            if (string.Equals(next, _ownedSymbol, StringComparison.OrdinalIgnoreCase)) return;
            var previous = _ownedSymbol;
            _ownedSymbol = null;
            if (previous != null)
            {
                await _marketData.UnsubscribeDepthAsync(previous);
                await _marketData.UnsubscribeQuotesAsync(previous);
            }

            _currentQuote = null;
            _currentDepth = null;
            Symbol = next;
            Rows.Clear();
            Status = $"{next} · requesting quote and L2 depth";
            await _marketData.SubscribeQuotesAsync(next);
            await _marketData.SubscribeDepthAsync(next);
            _ownedSymbol = next;
            var snapshot = await _marketData.GetSnapshotAsync(next);
            if (snapshot != null) _currentQuote = snapshot;
            QueueRebuild();
        }
        finally
        {
            _symbolGate.Release();
        }
    }

    private void OnQuote(Quote quote)
    {
        if (!string.Equals(quote.Symbol, _ownedSymbol, StringComparison.OrdinalIgnoreCase)) return;
        _currentQuote = quote;
        QueueRebuild();
    }

    private void OnDepth(MarketDepth depth)
    {
        if (!string.Equals(depth.Symbol, _ownedSymbol, StringComparison.OrdinalIgnoreCase)) return;
        _currentDepth = depth;
        QueueRebuild();
    }

    public void ZoomIn()
    {
        if (_binIndex == 0) return;
        _binIndex--;
        UpdateLabels();
        Rebuild();
    }

    public void ZoomOut()
    {
        if (_binIndex == BinTickSteps.Length - 1) return;
        _binIndex++;
        UpdateLabels();
        Rebuild();
    }

    public void ExpandLevels()
    {
        var next = Math.Clamp(VisibleLevels + 20, 20, 300);
        if (next == VisibleLevels) return;
        VisibleLevels = next;
        UpdateLabels();
        Rebuild();
    }

    public void ContractLevels()
    {
        var next = Math.Clamp(VisibleLevels - 20, 20, 300);
        if (next == VisibleLevels) return;
        VisibleLevels = next;
        UpdateLabels();
        Rebuild();
    }

    public void ResetScale()
    {
        _binIndex = 0;
        VisibleLevels = 80;
        UpdateLabels();
        Rebuild();
    }

    public void SetViewportHeight(double height)
    {
        if (height <= 0 || Math.Abs(height - _viewportHeight) < 1) return;
        _viewportHeight = height;
        UpdateRowHeight();
    }

    private void OnDomUpdated()
        => QueueRebuild();

    private void QueueRebuild()
    {
        if (_updateQueued || _disposed) return;
        _updateQueued = true;
        _dispatcher.BeginInvoke(new Action(() =>
        {
            _updateQueued = false;
            Rebuild();
        }), DispatcherPriority.Render);
    }

    internal void Rebuild()
    {
        var depth = _isEditable ? _currentDepth : _domService.CurrentDepth;
        var quote = _isEditable ? _currentQuote : _domService.CurrentQuote;
        if (!_isEditable) Symbol = _domService.SymbolInfo.Symbol;
        if (depth == null || !depth.HasRealDepth || depth.Bids.Count + depth.Asks.Count == 0)
        {
            Rows.Clear();
            LargestOrder = 0;
            VisibleLiquidity = 0;
            Status = SymbolClassifier.IsFutureSymbol(Symbol)
                ? $"{Symbol} · futures L2 is not available from the Schwab Trader API"
                : $"{Symbol} · no L2 snapshot (market closed or entitlement unavailable)";
            return;
        }

        var symbolTick = _isEditable ? 0.01m : _domService.SymbolInfo.TickSize;
        var tick = symbolTick > 0 ? symbolTick : 0.01m;
        var binTicks = BinTickSteps[_binIndex];
        var binSize = tick * binTicks;
        var anchor = quote?.Mid > 0 ? quote.Mid
            : quote?.Last > 0 ? quote.Last
            : (depth.Bids.FirstOrDefault()?.Price + depth.Asks.FirstOrDefault()?.Price) / 2m ?? 0m;
        if (anchor <= 0)
        {
            Rows.Clear();
            Status = $"{Symbol} · no price anchor";
            return;
        }

        var buckets = new Dictionary<decimal, (int Bid, int Ask)>();
        foreach (var bid in depth.Bids)
            AddToBucket(buckets, Bucket(bid.Price, binSize), bid.BidSize, 0);
        foreach (var ask in depth.Asks)
            AddToBucket(buckets, Bucket(ask.Price, binSize), 0, ask.AskSize);

        var center = Bucket(anchor, binSize);
        var rowCount = Math.Clamp(VisibleLevels, 20, 300);
        var above = rowCount / 2;
        var prices = Enumerable.Range(0, rowCount)
            .Select(i => center + (above - i) * binSize)
            .ToArray();
        var visibleSizes = prices.Select(p => buckets.GetValueOrDefault(p)).ToArray();
        var totals = visibleSizes.Select(x => x.Bid + x.Ask).Where(x => x > 0).Order().ToArray();
        var max = totals.DefaultIfEmpty(0).Max();
        var median = Median(totals);
        LargestOrder = max;
        VisibleLiquidity = totals.Sum();

        for (var i = 0; i < prices.Length; i++)
        {
            var price = prices[i];
            var size = visibleSizes[i];
            var total = size.Bid + size.Ask;
            var row = i < Rows.Count ? Rows[i] : AddRow();
            row.Price = price;
            row.PriceDisplay = FormatPriceBin(price, binSize, tick);
            row.BidSize = size.Bid;
            row.AskSize = size.Ask;
            row.TotalSize = total;
            row.BidWidth = BarWidth(size.Bid, max);
            row.AskWidth = BarWidth(size.Ask, max);
            row.BidBrush = SideBrush(size.Bid, max, true);
            row.AskBrush = SideBrush(size.Ask, max, false);
            row.HeatBrush = ConcentrationBrush(total, median);
            row.Concentration = ConcentrationLabel(total, median);
            row.IsMarketPrice = price == center;
            row.PriceBrush = row.IsMarketPrice ? Frozen(Color.FromRgb(255, 225, 55)) : Brushes.White;
        }
        while (Rows.Count > prices.Length) Rows.RemoveAt(Rows.Count - 1);

        UpdateRowHeight();
        var populated = totals.Length;
        Status = $"{Symbol} · {depth.Bids.Count} bid / {depth.Asks.Count} ask levels · " +
                 $"{populated} populated bins · max {max:N0} · median {median:N0}";
    }

    private DepthHeatRow AddRow()
    {
        var row = new DepthHeatRow();
        Rows.Add(row);
        return row;
    }

    private void UpdateLabels()
    {
        var ticks = BinTickSteps[_binIndex];
        BinLabel = ticks == 1 ? "1 tick / row" : $"{ticks} ticks / row";
        LevelLabel = $"{VisibleLevels} levels";
        UpdateRowHeight();
    }

    private void UpdateRowHeight() =>
        RowHeight = Math.Clamp((_viewportHeight - 2) / Math.Max(1, Math.Min(VisibleLevels, 80)), 8, 22);

    private static decimal Bucket(decimal price, decimal size) =>
        size <= 0 ? price : Math.Floor(price / size) * size;

    private static void AddToBucket(Dictionary<decimal, (int Bid, int Ask)> buckets,
        decimal price, int bid, int ask)
    {
        var current = buckets.GetValueOrDefault(price);
        buckets[price] = (current.Bid + Math.Max(0, bid), current.Ask + Math.Max(0, ask));
    }

    private static string FormatPriceBin(decimal low, decimal bin, decimal tick)
    {
        if (bin <= tick) return low.ToString("F2");
        var high = low + bin - tick;
        return $"{low:F2}–{high:F2}";
    }

    private static int Median(int[] sorted)
    {
        if (sorted.Length == 0) return 0;
        var middle = sorted.Length / 2;
        return sorted.Length % 2 == 0 ? (sorted[middle - 1] + sorted[middle]) / 2 : sorted[middle];
    }

    private static double BarWidth(int size, int max)
    {
        if (size <= 0 || max <= 0) return 0;
        return 8 + Math.Sqrt((double)size / max) * 150;
    }

    private static Brush SideBrush(int size, int max, bool bid)
    {
        if (size <= 0 || max <= 0) return Brushes.Transparent;
        var intensity = Math.Sqrt(Math.Clamp((double)size / max, 0, 1));
        return bid
            ? Frozen(Color.FromArgb((byte)(80 + intensity * 175), 0, (byte)(125 + intensity * 100), 95))
            : Frozen(Color.FromArgb((byte)(80 + intensity * 175), (byte)(175 + intensity * 80), 45, 35));
    }

    private static Brush ConcentrationBrush(int size, int median)
    {
        if (size <= 0 || median <= 0) return Brushes.Transparent;
        var multiple = (double)size / median;
        return multiple switch
        {
            >= 8 => Frozen(Color.FromArgb(175, 255, 32, 0)),
            >= 4 => Frozen(Color.FromArgb(145, 255, 115, 0)),
            >= 2 => Frozen(Color.FromArgb(115, 255, 220, 0)),
            >= 1 => Frozen(Color.FromArgb(70, 0, 180, 255)),
            _ => Frozen(Color.FromArgb(35, 0, 95, 145))
        };
    }

    private static string ConcentrationLabel(int size, int median)
    {
        if (size <= 0 || median <= 0) return "";
        var multiple = (double)size / median;
        return multiple >= 8 ? "EXTREME" : multiple >= 4 ? "LARGE" : multiple >= 2 ? "HEAVY" : "";
    }

    private static Brush Frozen(Color color)
    {
        var brush = new SolidColorBrush(color);
        brush.Freeze();
        return brush;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        if (!_isEditable) _domService.DomUpdated -= OnDomUpdated;
        _quoteSubscription?.Dispose();
        _depthSubscription?.Dispose();
        var symbol = _ownedSymbol;
        _ownedSymbol = null;
        if (symbol != null)
        {
            _ = _marketData.UnsubscribeDepthAsync(symbol);
            _ = _marketData.UnsubscribeQuotesAsync(symbol);
        }
        _symbolGate.Dispose();
    }
}
