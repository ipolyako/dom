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
    [ObservableProperty] private int _visibleLevels = 40;
    [ObservableProperty] private bool _isLocked;
    [ObservableProperty] private decimal _clickedPrice;
    [ObservableProperty] private bool _hasDepth;

    public ObservableCollection<DomLadderRow> Rows { get; } = [];
    public ObservableCollection<OrderState> WorkingOrders { get; } = [];

    public string? CurrentAccountId { get; set; }
    public Position? CurrentPosition { get; set; }

    public event Action<decimal, OrderSide, OrderType>? PriceLevelClicked;
    public event Action<OrderState, decimal>? OrderMoveRequested;
    public event Action<decimal>? ContextMenuRequested;
    public event Action<string>? DragError;

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
        RebuildLadder();
    }

    public void RefreshOrders(IEnumerable<OrderState> orders)
    {
        WorkingOrders.Clear();
        foreach (var o in orders.Where(o => o.IsWorking && o.Symbol == Symbol))
            WorkingOrders.Add(o);
        _pendingUpdate = true;
    }

    private void RebuildLadder()
    {
        var rows = _domService.BuildLadder(VisibleLevels, WorkingOrders, CurrentPosition);
        HasDepth = rows.FirstOrDefault()?.HasRealDepth ?? false;

        // Update existing rows in place to minimize UI churn
        for (int i = 0; i < rows.Count; i++)
        {
            if (i < Rows.Count)
                CopyRow(rows[i], Rows[i]);
            else
                Rows.Add(rows[i]);
        }
        while (Rows.Count > rows.Count)
            Rows.RemoveAt(Rows.Count - 1);
    }

    private static void CopyRow(DomLadderRow src, DomLadderRow dst)
    {
        dst.Price      = src.Price;
        dst.BidSize    = src.BidSize;
        dst.AskSize    = src.AskSize;
        dst.IsBid      = src.IsBid;
        dst.IsAsk      = src.IsAsk;
        dst.IsLast     = src.IsLast;
        dst.IsPosition = src.IsPosition;

        dst.BuyOrders.Clear();
        foreach (var o in src.BuyOrders) dst.BuyOrders.Add(o);

        dst.SellOrders.Clear();
        foreach (var o in src.SellOrders) dst.SellOrders.Add(o);
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

    // Mirrors the pinning logic in DomService.BuildLadder so market orders cancel correctly.
    private static bool IsOrderAtDisplayPrice(OrderState o, decimal price, FastDOM.MarketData.Models.Quote? q, FastDOM.Core.Models.SymbolInfo si)
    {
        if (o.LimitPrice.HasValue)
            return si.RoundToTick(o.LimitPrice.Value) == price;
        if (q == null) return false;
        var pin = o.Side == OrderSide.Buy
            ? (q.Ask > 0 ? q.Ask : q.Last)
            : (q.Bid > 0 ? q.Bid : q.Last);
        return si.RoundToTick(pin) == price;
    }

    // Returns true if there are working orders on the buy side at this displayed ladder price
    public bool HasBuyOrderAt(decimal price) => HasWorkingOrderAtPrice(OrderSide.Buy, price);

    // Returns true if there are working orders on the sell side at this displayed ladder price
    public bool HasSellOrderAt(decimal price) => HasWorkingOrderAtPrice(OrderSide.Sell, price);

    // Called when user drags an order marker to a new price
    public void OnOrderDragged(OrderState order, decimal newPrice)
    {
        if (order.Side is not OrderSide.Buy and not OrderSide.Sell)
            return;

        OrderMoveRequested?.Invoke(order, newPrice);
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

    private static bool HasMatchingDisplayPrice(
        OrderState o,
        decimal targetPrice,
        FastDOM.MarketData.Models.Quote? q,
        FastDOM.Core.Models.SymbolInfo si)
    {
        if (o.LimitPrice.HasValue)
            return si.RoundToTick(o.LimitPrice.Value) == targetPrice;

        if (q == null) return false;
        var pin = o.Side == OrderSide.Buy
            ? (q.Ask > 0 ? q.Ask : q.Last)
            : (q.Bid > 0 ? q.Bid : q.Last);
        return si.RoundToTick(pin) == targetPrice;
    }
}
