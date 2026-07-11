using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Media;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using FastDOM.App.Services;
using FastDOM.MarketData.Models;

namespace FastDOM.App.ViewModels;

public partial class DepthMapRow : ObservableObject
{
    [ObservableProperty] private decimal _price;
    [ObservableProperty] private decimal _lowPrice;
    [ObservableProperty] private decimal _highPrice;
    [ObservableProperty] private int _bidSize;
    [ObservableProperty] private int _askSize;
    [ObservableProperty] private int _totalSize;
    [ObservableProperty] private double _bidWidth;
    [ObservableProperty] private double _askWidth;
    [ObservableProperty] private double _heatWidth;
    [ObservableProperty] private Brush _bidBrush = Brushes.Transparent;
    [ObservableProperty] private Brush _askBrush = Brushes.Transparent;
    [ObservableProperty] private Brush _heatBrush = Brushes.Transparent;
    [ObservableProperty] private Brush _sizeBrush = Brushes.Transparent;
    [ObservableProperty] private bool _isCurrentPrice;
    [ObservableProperty] private string _significanceLabel = "";

    public string BidDisplay => BidSize > 0 ? BidSize.ToString("N0") : "";
    public string AskDisplay => AskSize > 0 ? AskSize.ToString("N0") : "";
    public string SizeDisplay => TotalSize > 0
        ? string.IsNullOrWhiteSpace(SignificanceLabel)
            ? TotalSize.ToString("N0")
            : $"{TotalSize:N0} {SignificanceLabel}"
        : "";
    public string PriceDisplay => Price.ToString("F2");

    partial void OnBidSizeChanged(int value) => OnPropertyChanged(nameof(BidDisplay));
    partial void OnAskSizeChanged(int value) => OnPropertyChanged(nameof(AskDisplay));
    partial void OnTotalSizeChanged(int value) => OnPropertyChanged(nameof(SizeDisplay));
    partial void OnSignificanceLabelChanged(string value) => OnPropertyChanged(nameof(SizeDisplay));
    partial void OnPriceChanged(decimal value) => OnPropertyChanged(nameof(PriceDisplay));
    partial void OnLowPriceChanged(decimal value) => OnPropertyChanged(nameof(PriceDisplay));
    partial void OnHighPriceChanged(decimal value) => OnPropertyChanged(nameof(PriceDisplay));
}

public partial class DepthMapViewModel : ObservableObject
{
    private readonly DomService _domService;
    private readonly Dispatcher _dispatcher;
    private static readonly int[] ScaleTicks = [1, 2, 5, 10, 25, 50, 100];
    private static readonly decimal[] RangePercents = [0.001m, 0.0025m, 0.005m, 0.01m, 0.02m, 0.04m, 0.08m];
    private static readonly double[] RowHeights = [6, 7, 8, 10, 12, 14, 16];
    private bool _pendingUpdate;
    private int _scaleIndex;
    private int _rangeIndex = 2;
    private int _rowHeightIndex = 3;
    private int _viewportRowLimit = 70;
    private double _viewportHeight;

    [ObservableProperty] private string _title = "L2 HEAT";
    [ObservableProperty] private string _status = "Waiting for L2";
    [ObservableProperty] private int _levelCount;
    [ObservableProperty] private int _maxSize;
    [ObservableProperty] private string _scaleLabel = "feed";
    [ObservableProperty] private double _rowHeight = 10;

    public ObservableCollection<DepthMapRow> Rows { get; } = [];

    public DepthMapViewModel(DomService domService)
    {
        _domService = domService;
        _dispatcher = Dispatcher.CurrentDispatcher;
        _domService.DomUpdated += OnDomUpdated;
    }

    public void Compress()
    {
        if (_rowHeightIndex < RowHeights.Length - 1)
        {
            _rowHeightIndex++;
        }
        else if (_rangeIndex < RangePercents.Length - 1)
        {
            _rangeIndex++;
        }
        else
        {
            return;
        }

        UpdateScaleVisuals();
        UpdateViewportRowLimit();
        Rebuild();
    }

    public void Expand()
    {
        if (_rowHeightIndex > 0)
        {
            _rowHeightIndex--;
        }
        else if (_rangeIndex > 0)
        {
            _rangeIndex--;
        }
        else
        {
            return;
        }

        UpdateScaleVisuals();
        UpdateViewportRowLimit();
        Rebuild();
    }

    public void ResetScale()
    {
        if (_scaleIndex == 0 && _rangeIndex == 2 && _rowHeightIndex == 3) return;
        _scaleIndex = 0;
        _rangeIndex = 2;
        _rowHeightIndex = 3;
        UpdateScaleVisuals();
        UpdateViewportRowLimit();
        Rebuild();
    }

    public void SetViewportHeight(double height)
    {
        if (height <= 0) return;
        _viewportHeight = height;
        if (!UpdateViewportRowLimit()) return;

        Rebuild();
    }

    private void UpdateScaleVisuals()
    {
        RowHeight = RowHeights[_rowHeightIndex];
        ScaleLabel = $"feed {RowHeight:0}px";
    }

    private bool UpdateViewportRowLimit()
    {
        if (_viewportHeight <= 0) return false;
        var rowLimit = Math.Clamp((int)Math.Floor(_viewportHeight / RowHeight), 24, 260);
        if (rowLimit == _viewportRowLimit) return false;

        _viewportRowLimit = rowLimit;
        return true;
    }

    private void OnDomUpdated()
    {
        if (_pendingUpdate) return;
        _pendingUpdate = true;
        _dispatcher.BeginInvoke(new Action(() =>
        {
            _pendingUpdate = false;
            Rebuild();
        }), DispatcherPriority.Background);
    }

    private void Rebuild()
    {
        var depth = _domService.CurrentDepth;
        if (depth == null || !depth.HasRealDepth || (depth.Bids.Count == 0 && depth.Asks.Count == 0))
        {
            Rows.Clear();
            LevelCount = 0;
            MaxSize = 0;
            Status = "No L2 depth";
            return;
        }

        var quote = _domService.CurrentQuote;
        var currentPrice = quote?.Last > 0
            ? quote.Last
            : quote?.Mid > 0
                ? quote.Mid
                : depth.Bids.Concat(depth.Asks).Select(x => x.Price).DefaultIfEmpty(0).Average();
        if (currentPrice <= 0)
        {
            Rows.Clear();
            LevelCount = 0;
            MaxSize = 0;
            Status = "No price anchor";
            return;
        }

        var byPrice = new Dictionary<decimal, (int bid, int ask)>();
        var tick = _domService.SymbolInfo.TickSize;
        if (tick <= 0) tick = 0.01m;
        var requestedScaleTicks = ScaleTicks[_scaleIndex];
        var rangePercent = RangePercents[_rangeIndex];
        var feedLow = depth.Bids.Concat(depth.Asks).Select(x => x.Price).DefaultIfEmpty(currentPrice).Min();
        var feedHigh = depth.Bids.Concat(depth.Asks).Select(x => x.Price).DefaultIfEmpty(currentPrice).Max();
        var percentLow = currentPrice * (1 - rangePercent);
        var percentHigh = currentPrice * (1 + rangePercent);
        var lowRange = Math.Min(feedLow, percentLow);
        var highRange = Math.Max(feedHigh, percentHigh);
        var minScaleTicks = Math.Max(1, (int)Math.Ceiling((double)((highRange - lowRange) / tick) / _viewportRowLimit));
        var effectiveScaleTicks = Math.Max(requestedScaleTicks, minScaleTicks);
        var bucketTick = tick * effectiveScaleTicks;
        var currentBucket = RoundToBucket(currentPrice, bucketTick);

        foreach (var b in depth.Bids)
        {
            if (b.Price < lowRange || b.Price > highRange) continue;
            var price = RoundToBucket(b.Price, bucketTick);
            var existing = byPrice.GetValueOrDefault(price);
            byPrice[price] = (existing.bid + b.BidSize, existing.ask);
        }

        foreach (var a in depth.Asks)
        {
            if (a.Price < lowRange || a.Price > highRange) continue;
            var price = RoundToBucket(a.Price, bucketTick);
            var existing = byPrice.GetValueOrDefault(price);
            byPrice[price] = (existing.bid, existing.ask + a.AskSize);
        }

        var max = byPrice.Values.Select(v => Math.Max(v.bid, v.ask)).DefaultIfEmpty(0).Max();
        var baseline = BaselineSize(byPrice.Values.Select(v => v.bid + v.ask));
        MaxSize = max;

        var low = RoundToBucket(lowRange, bucketTick);
        var high = RoundToBucket(highRange, bucketTick);
        var prices = BuildContinuousPriceRange(high, low, bucketTick);
        LevelCount = prices.Count;
        ScaleLabel = $"feed {RowHeight:0}px";
        Status = effectiveScaleTicks == requestedScaleTicks
            ? $"REAL L2  {depth.Bids.Count}x{depth.Asks.Count} feed  {LevelCount} rows  {effectiveScaleTicks}t  normal {baseline:N0}  max {MaxSize:N0}"
            : $"REAL L2  {depth.Bids.Count}x{depth.Asks.Count} feed  {LevelCount} rows  A{effectiveScaleTicks}t  normal {baseline:N0}  max {MaxSize:N0}";
        var cumulativeByPrice = BuildCumulativeLiquidity(prices, byPrice, currentBucket);
        SyncRows(prices, byPrice, cumulativeByPrice, max, baseline, bucketTick, currentBucket);
    }

    private List<decimal> BuildContinuousPriceRange(decimal high, decimal low, decimal tick)
    {
        var prices = new List<decimal>();
        var guard = 0;
        for (var price = high; price >= low && guard < 2000; price -= tick, guard++)
            prices.Add(_domService.SymbolInfo.RoundToTick(price));
        return prices;
    }

    private static Dictionary<decimal, int> BuildCumulativeLiquidity(
        IReadOnlyList<decimal> prices,
        Dictionary<decimal, (int bid, int ask)> byPrice,
        decimal currentBucket)
    {
        var cumulative = new Dictionary<decimal, int>(prices.Count);

        var askRunning = 0;
        foreach (var price in prices.Order())
        {
            if (price < currentBucket) continue;
            byPrice.TryGetValue(price, out var sizes);
            askRunning += sizes.ask;
            cumulative[price] = askRunning;
        }

        var bidRunning = 0;
        foreach (var price in prices.OrderDescending())
        {
            if (price > currentBucket) continue;
            byPrice.TryGetValue(price, out var sizes);
            bidRunning += sizes.bid;
            cumulative[price] = bidRunning;
        }

        return cumulative;
    }

    private void SyncRows(
        IReadOnlyList<decimal> prices,
        Dictionary<decimal, (int bid, int ask)> byPrice,
        Dictionary<decimal, int> cumulativeByPrice,
        int max,
        int baseline,
        decimal bucketTick,
        decimal currentBucket)
    {
        for (var i = 0; i < prices.Count; i++)
        {
            var price = prices[i];
            byPrice.TryGetValue(price, out var sizes);
            var row = i < Rows.Count ? Rows[i] : AddRow();
            row.Price = price;
            row.HighPrice = price + bucketTick - _domService.SymbolInfo.TickSize;
            row.LowPrice = price;
            row.BidSize = sizes.bid;
            row.AskSize = sizes.ask;
            row.TotalSize = cumulativeByPrice.GetValueOrDefault(price);
            row.BidWidth = HeatWidth(sizes.bid, max);
            row.AskWidth = HeatWidth(sizes.ask, max);
            row.HeatWidth = HeatWidth(row.TotalSize, max);
            row.BidBrush = HeatBrush(sizes.bid, max, isBid: true);
            row.AskBrush = HeatBrush(sizes.ask, max, isBid: false);
            row.HeatBrush = LiquidityHeatBrush(row.TotalSize, baseline);
            row.SizeBrush = SizeBrush(sizes.bid, sizes.ask);
            row.SignificanceLabel = SignificanceLabel(row.TotalSize, baseline);
            row.IsCurrentPrice = price == currentBucket;
        }

        while (Rows.Count > prices.Count)
            Rows.RemoveAt(Rows.Count - 1);
    }

    private DepthMapRow AddRow()
    {
        var row = new DepthMapRow();
        Rows.Add(row);
        return row;
    }

    private static decimal RoundToBucket(decimal price, decimal bucketTick)
    {
        if (bucketTick <= 0) return price;
        return Math.Floor(price / bucketTick) * bucketTick;
    }

    private static string RangeLabel(decimal rangePercent) =>
        $"+/-{rangePercent * 100m:0.##}%";

    private static double HeatWidth(int size, int max)
    {
        if (size <= 0 || max <= 0) return 0;
        var normalized = Math.Clamp((double)size / max, 0.0, 1.0);
        return 10 + Math.Sqrt(normalized) * 170;
    }

    private static Brush HeatBrush(int size, int max, bool isBid)
    {
        if (size <= 0 || max <= 0) return Brushes.Transparent;
        var normalized = Math.Clamp((double)size / max, 0.0, 1.0);
        var alpha = (byte)(45 + Math.Sqrt(normalized) * 210);
        var color = isBid
            ? Color.FromArgb(alpha, 24, (byte)(95 + normalized * 105), (byte)(150 + normalized * 95))
            : Color.FromArgb(alpha, (byte)(180 + normalized * 75), (byte)(45 + normalized * 50), 24);
        var brush = new SolidColorBrush(color);
        brush.Freeze();
        return brush;
    }

    private static int BaselineSize(IEnumerable<int> sizes)
    {
        var nonZero = sizes.Where(x => x > 0).Order().ToArray();
        if (nonZero.Length == 0) return 0;
        var mid = nonZero.Length / 2;
        return nonZero.Length % 2 == 0
            ? Math.Max(1, (nonZero[mid - 1] + nonZero[mid]) / 2)
            : Math.Max(1, nonZero[mid]);
    }

    private static Brush LiquidityHeatBrush(int size, int baseline)
    {
        if (size <= 0 || baseline <= 0) return Brushes.Transparent;

        var multiple = (double)size / baseline;
        Color color;
        if (multiple >= 10)
        {
            color = Color.FromArgb(250, 255, 44, 0);
        }
        else if (multiple >= 6)
        {
            color = Color.FromArgb(235, 255, 130, 0);
        }
        else if (multiple >= 3)
        {
            color = Color.FromArgb(215, 255, 220, 0);
        }
        else if (multiple >= 1.5)
        {
            color = Color.FromArgb(145, 0, 210, 255);
        }
        else
        {
            color = Color.FromArgb(55, 0, 115, 165);
        }

        var brush = new SolidColorBrush(color);
        brush.Freeze();
        return brush;
    }

    private static string SignificanceLabel(int size, int baseline)
    {
        if (size <= 0 || baseline <= 0) return "";
        var multiple = (double)size / baseline;
        return multiple switch
        {
            >= 10 => "!!!",
            >= 6 => "!!",
            >= 3 => "!",
            _ => ""
        };
    }

    private static Brush SizeBrush(int bid, int ask)
    {
        if (bid <= 0 && ask <= 0) return Brushes.Transparent;
        var color = bid >= ask
            ? Color.FromRgb(93, 235, 128)
            : Color.FromRgb(255, 118, 118);
        var brush = new SolidColorBrush(color);
        brush.Freeze();
        return brush;
    }
}
