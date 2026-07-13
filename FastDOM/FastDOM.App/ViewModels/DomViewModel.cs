using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Input;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using FastDOM.App.Services;
using FastDOM.Core.Enums;
using FastDOM.Core.Models;
using FastDOM.Infrastructure.Config;
using Microsoft.Extensions.Logging;

namespace FastDOM.App.ViewModels;

public partial class DomViewModel : ObservableObject
{
    private readonly ILogger<DomViewModel> _logger;
    private readonly DomService _domService;
    private readonly OrderService _orderService;
    private readonly ConfigManager _config;
    private readonly DispatcherTimer _refreshTimer;

    [ObservableProperty] private string _symbol = "SPY";
    [ObservableProperty] private int _visibleLevels = 120;
    [ObservableProperty] private int _priceStepTicks = 1;
    [ObservableProperty] private bool _isLocked;
    [ObservableProperty] private decimal _clickedPrice;
    [ObservableProperty] private bool _hasDepth;

    // Live drag state — updated by DomView on mouse move so the target row
    // and side highlight in real time as the user drags.
    [ObservableProperty] private decimal? _dragTargetPrice;
    [ObservableProperty] private OrderSide? _dragTargetSide;
    [ObservableProperty] private string _dragPreviewSummary = "";

    public ObservableCollection<DomLadderRow> Rows { get; } = [];
    public ObservableCollection<OrderState> WorkingOrders { get; } = [];
    public string PriceStepDisplay => PriceStepTicks == 1 ? "1t" : $"{PriceStepTicks}t";

    public string? CurrentAccountId { get; set; }
    public Position? CurrentPosition { get; set; }

    public event Action<decimal, OrderSide, OrderType>? PriceLevelClicked;
    public event Action<OrderState, decimal, decimal>? OrderMoveRequested;
    public event Action<decimal>? ContextMenuRequested;
    public event Action<string>? DragError;
    public event Action? RowsUpdated;
    public event Action? CenterRequested;

    public DomViewModel(ILogger<DomViewModel> logger, DomService domService,
                        OrderService orderService, ConfigManager config)
    {
        _logger = logger;
        _domService = domService;
        _orderService = orderService;
        _config = config;

        VisibleLevels = config.AppSettings.DomVisibleLevels;

        _domService.DomUpdated += OnDomUpdated;

        // Throttle DOM redraws to ~30 fps
        _refreshTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(33) };
        _refreshTimer.Tick += (_, _) => FlushPendingUpdate();
        _refreshTimer.Start();
    }

    private bool _pendingUpdate;

    private void OnDomUpdated() => _pendingUpdate = true;

    private void FlushPendingUpdate()
    {
        if (!_pendingUpdate) return;
        _pendingUpdate = false;
        if (IsLocked)
            _domService.CenterLadderOnLast();
        else
            _domService.CenterLadderOnLastIfOutsideVisibleRange(VisibleLevels, PriceStepTicks);
        RebuildLadder();
    }

    public void CenterLadderOnLast()
    {
        _domService.CenterLadderOnLast();
        RebuildLadder();
    }

    public void CenterLadderOnLastInView()
    {
        CenterLadderOnLast();
        CenterRequested?.Invoke();
    }

    partial void OnIsLockedChanged(bool value)
    {
        if (value)
            CenterLadderOnLast();
    }

    public void RefreshOrders(IEnumerable<OrderState> orders)
    {
        WorkingOrders.Clear();
        foreach (var o in orders.Where(o => o.IsWorking && o.Symbol == Symbol))
            WorkingOrders.Add(o);
        _pendingUpdate = true;
    }

    public void SetCurrentPosition(Position? position)
    {
        CurrentPosition = position;
        RebuildLadder();
    }

    // When the DOM's symbol changes, re-filter WorkingOrders against the new
    // symbol so any pre-existing orders on that ticker appear immediately —
    // without waiting for the next OrderStateChanged event.
    partial void OnSymbolChanged(string value)
    {
        var seen = new HashSet<string>();
        WorkingOrders.Clear();
        foreach (var o in _orderService.ActiveOrders.Values)
        {
            if (!o.IsWorking || o.Symbol != value) continue;
            var key = o.BrokerOrderId ?? o.ClientOrderId;
            if (seen.Add(key)) WorkingOrders.Add(o);
        }
        _pendingUpdate = true;
    }

    private void RebuildLadder()
    {
        _domService.PopulateLadder(Rows, VisibleLevels, PriceStepTicks, WorkingOrders, CurrentPosition);
        HasDepth = Rows.Count > 0 && Rows[0].HasRealDepth;
        RowsUpdated?.Invoke();
    }

    partial void OnVisibleLevelsChanged(int value)
    {
        var clamped = Math.Clamp(value, 20, 400);
        if (clamped != value)
        {
            VisibleLevels = clamped;
            return;
        }

        _pendingUpdate = true;
    }

    [RelayCommand]
    private void IncreaseDepth()
    {
        VisibleLevels = Math.Min(400, VisibleLevels + 100);
        CenterLadderOnLast();
        CenterRequested?.Invoke();
    }

    [RelayCommand]
    private void DecreaseDepth()
    {
        VisibleLevels = Math.Max(20, VisibleLevels - 100);
        CenterLadderOnLast();
        CenterRequested?.Invoke();
    }

    private static readonly int[] StepChoices = [1, 2, 5, 10, 25, 50, 100];

    partial void OnPriceStepTicksChanged(int value)
    {
        var clamped = StepChoices.Contains(value) ? value : 1;
        if (clamped != value)
        {
            PriceStepTicks = clamped;
            return;
        }

        OnPropertyChanged(nameof(PriceStepDisplay));
        _pendingUpdate = true;
    }

    [RelayCommand]
    private void ZoomIn()
    {
        var idx = Array.IndexOf(StepChoices, PriceStepTicks);
        PriceStepTicks = StepChoices[Math.Max(0, idx - 1)];
    }

    [RelayCommand]
    private void ZoomOut()
    {
        var idx = Array.IndexOf(StepChoices, PriceStepTicks);
        PriceStepTicks = StepChoices[Math.Min(StepChoices.Length - 1, Math.Max(0, idx) + 1)];
    }

    [RelayCommand]
    private void ResetZoom()
    {
        PriceStepTicks = 1;
    }

    // Called by DOM view on left-click buy column
    public void OnBuyColumnClicked(decimal price, ModifierKeys modifiers)
    {
        ClickedPrice = price;
        var orderType = modifiers switch
        {
            ModifierKeys.Shift   => OrderType.StopMarket,
            ModifierKeys.Control => OrderType.StopLimit,
            ModifierKeys.Alt     => OrderType.Bracket,
            _                    => OrderType.Limit
        };
        PriceLevelClicked?.Invoke(price, OrderSide.Buy, orderType);
    }

    // Called by DOM view on left-click sell column
    public void OnSellColumnClicked(decimal price, ModifierKeys modifiers)
    {
        ClickedPrice = price;
        var orderType = modifiers switch
        {
            ModifierKeys.Shift   => OrderType.StopMarket,
            ModifierKeys.Control => OrderType.StopLimit,
            ModifierKeys.Alt     => OrderType.Bracket,
            _                    => OrderType.Limit
        };
        PriceLevelClicked?.Invoke(price, OrderSide.Sell, orderType);
    }

    [RelayCommand]
    private async Task CancelAtPrice(decimal price) => await CancelOrdersAtPriceAsync(price);

    // Cancel all working orders at a specific price level (called from DOM view)
    public async Task CancelOrdersAtPriceAsync(decimal price)
    {
        if (CurrentAccountId == null) return;
        var q  = _domService.CurrentQuote;
        var si = _domService.SymbolInfo;
        var toCancel = WorkingOrders
            .Where(o => o.BrokerOrderId != null && IsOrderAtDisplayPrice(o, price, q, si))
            .ToList();
        foreach (var o in toCancel)
            await _orderService.CancelOrderAsync(CurrentAccountId, o.BrokerOrderId!);
    }

    public async Task CancelOrderByIdAsync(string brokerOrderId)
    {
        if (CurrentAccountId == null || string.IsNullOrWhiteSpace(brokerOrderId)) return;
        await _orderService.CancelOrderAsync(CurrentAccountId, brokerOrderId);
    }

    // Mirrors the pinning logic in DomService.BuildLadder so market orders cancel correctly.
    private bool IsOrderAtDisplayPrice(OrderState o, decimal price, FastDOM.MarketData.Models.Quote? q, FastDOM.Core.Models.SymbolInfo si)
    {
        var step = si.TickSize * Math.Max(1, PriceStepTicks);
        if (o.LimitPrice.HasValue)
        {
            if (DomService.BucketPrice(si.RoundToTick(o.LimitPrice.Value), step) == price)
                return true;
        }
        if (o.StopPrice.HasValue)
        {
            if (DomService.BucketPrice(si.RoundToTick(o.StopPrice.Value), step) == price)
                return true;
        }
        if (q == null) return false;
        var pin = o.Side == OrderSide.Buy
            ? (q.Ask > 0 ? q.Ask : q.Last)
            : (q.Bid > 0 ? q.Bid : q.Last);
        return DomService.BucketPrice(si.RoundToTick(pin), step) == price;
    }

    // Returns true if there are working orders on the buy side at this displayed ladder price
    public bool HasBuyOrderAt(decimal price) => HasWorkingOrderAtPrice(OrderSide.Buy, price);

    // Returns true if there are working orders on the sell side at this displayed ladder price
    public bool HasSellOrderAt(decimal price) => HasWorkingOrderAtPrice(OrderSide.Sell, price);

    // Called when user drags an order marker to a new price
    public void OnOrderDragged(OrderState order, decimal fromPrice, decimal newPrice)
    {
        if (order.Side is not OrderSide.Buy and not OrderSide.Sell)
            return;

        OrderMoveRequested?.Invoke(order, fromPrice, newPrice);
    }

    public void ReportDragError(string message) => DragError?.Invoke(message);

    public void OnRightClick(decimal price) => ContextMenuRequested?.Invoke(price);

    private bool HasWorkingOrderAtPrice(OrderSide side, decimal price)
    {
        var q = _domService.CurrentQuote;
        var si = _domService.SymbolInfo;
        foreach (var o in WorkingOrders.Where(o => o.Side == side && o.IsWorking))
        {
            if (!HasMatchingDisplayPrice(o, price, q, si)) continue;
            return true;
        }

        return false;
    }

    private bool HasMatchingDisplayPrice(
        OrderState o,
        decimal targetPrice,
        FastDOM.MarketData.Models.Quote? q,
        FastDOM.Core.Models.SymbolInfo si)
    {
        var step = si.TickSize * Math.Max(1, PriceStepTicks);
        if (o.LimitPrice.HasValue)
        {
            if (DomService.BucketPrice(si.RoundToTick(o.LimitPrice.Value), step) == targetPrice)
                return true;
        }
        if (o.StopPrice.HasValue)
        {
            if (DomService.BucketPrice(si.RoundToTick(o.StopPrice.Value), step) == targetPrice)
                return true;
        }

        if (q == null) return false;
        var pin = o.Side == OrderSide.Buy
            ? (q.Ask > 0 ? q.Ask : q.Last)
            : (q.Bid > 0 ? q.Bid : q.Last);
        return DomService.BucketPrice(si.RoundToTick(pin), step) == targetPrice;
    }
}
