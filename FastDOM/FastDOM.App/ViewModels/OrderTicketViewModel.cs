using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using FastDOM.App.Services;
using FastDOM.Broker.Interfaces;
using FastDOM.Core.Enums;
using FastDOM.Core.Models;
using Microsoft.Extensions.Logging;

namespace FastDOM.App.ViewModels;

public partial class OrderTicketViewModel : ObservableObject
{
    private readonly ILogger<OrderTicketViewModel> _logger;
    private readonly OrderService _orderService;
    private readonly IBrokerClient _broker;
    private readonly AccountSummaryCache _accountCache;

    [ObservableProperty] private string _symbol = "";
    [ObservableProperty] private string _accountId = "";
    [ObservableProperty] private OrderSide _side = OrderSide.Buy;
    [ObservableProperty] private int _quantity = 100;
    [ObservableProperty] private OrderType _orderType = OrderType.Limit;
    [ObservableProperty] private decimal? _limitPrice;
    [ObservableProperty] private decimal? _stopPrice;
    [ObservableProperty] private TimeInForce _timeInForce = TimeInForce.Day;
    [ObservableProperty] private bool _extendedHours;
    [ObservableProperty] private string _statusMessage = "";
    [ObservableProperty] private decimal? _lastPrice;
    [ObservableProperty] private decimal? _atr;

    public List<OrderSide> OrderSides { get; } = [OrderSide.Buy, OrderSide.Sell];

    public List<OrderType> OrderTypes { get; } =
        [OrderType.Market, OrderType.Limit, OrderType.StopMarket, OrderType.StopLimit,
         OrderType.MarketableLimit, OrderType.Bracket];

    public List<TimeInForce> TifOptions { get; } = [TimeInForce.Day, TimeInForce.GTC, TimeInForce.IOC];

    public OrderTicketViewModel(ILogger<OrderTicketViewModel> logger,
        OrderService orderService, IBrokerClient broker, AccountSummaryCache accountCache)
    {
        _logger = logger;
        _orderService = orderService;
        _broker = broker;
        _accountCache = accountCache;
        // Default the Ext flag to true when the app opens outside regular trading hours
        // so pre/post-market clicks don't get silently rejected.
        _extendedHours = IsOutsideRegularHours(DateTime.UtcNow);
    }

    // Regular US equity session: 09:30–16:00 ET, Mon–Fri (holidays not handled here).
    private static bool IsOutsideRegularHours(DateTime utcNow)
    {
        var etZone = TimeZoneInfo.FindSystemTimeZoneById("Eastern Standard Time");
        var et = TimeZoneInfo.ConvertTimeFromUtc(utcNow, etZone);
        if (et.DayOfWeek is DayOfWeek.Saturday or DayOfWeek.Sunday) return true;
        var t = et.TimeOfDay;
        return t < new TimeSpan(9, 30, 0) || t >= new TimeSpan(16, 0, 0);
    }

    partial void OnOrderTypeChanged(OrderType value)
    {
        if (value == OrderType.Market)
        {
            LimitPrice = null;
            StopPrice = null;
        }
        else if (value == OrderType.StopMarket)
        {
            LimitPrice = null;
            StopPrice ??= DefaultStopPrice();
        }
        else if (LastPrice.HasValue && NeedsLimitPrice(value))
        {
            LimitPrice = LastPrice;
        }

        if (value == OrderType.StopLimit)
            StopPrice ??= DefaultStopPrice();

        if (IsStopType(value))
            AutoInferStopSide();
    }

    partial void OnLastPriceChanged(decimal? value)
    {
        if (OrderType == OrderType.StopMarket && !StopPrice.HasValue)
            StopPrice = DefaultStopPrice();

        if (NeedsLimitPrice(OrderType) && !LimitPrice.HasValue && value.HasValue)
            LimitPrice = value;
    }

    partial void OnStopPriceChanged(decimal? value)
    {
        if (IsStopType(OrderType))
            AutoInferStopSide();
    }

    private void AutoInferStopSide()
    {
        if (!StopPrice.HasValue || !LastPrice.HasValue || LastPrice <= 0) return;
        // Stop below market → sell stop (stop-loss on long); stop above → buy stop (breakout entry)
        Side = StopPrice < LastPrice ? OrderSide.Sell : OrderSide.Buy;
    }

    private static bool NeedsLimitPrice(OrderType t) =>
        t is OrderType.Limit or OrderType.StopLimit or OrderType.MarketableLimit or OrderType.Bracket;

    private static bool IsStopType(OrderType t) =>
        t is OrderType.StopMarket or OrderType.StopLimit;

    public void PopulateFromDomClick(decimal price, OrderSide side, OrderType orderType)
    {
        if (orderType == OrderType.StopMarket)
        {
            LimitPrice = null;
            StopPrice = price;
        }
        else if (orderType == OrderType.StopLimit)
        {
            StopPrice = price;
            LimitPrice ??= price;
        }
        else
        {
            StopPrice = null;
            LimitPrice = price;
        }

        Side = side;
        OrderType = orderType;
        StatusMessage = $"Ready: {side} {orderType} @ {price:F2}";
    }

    public void ResetForSymbol(string symbol)
    {
        Symbol = symbol;
        LimitPrice = null;
        StopPrice = null;
        LastPrice = null;
        StatusMessage = "";
    }

    private decimal? DefaultStopPrice()
    {
        if (!LastPrice.HasValue || LastPrice <= 0) return null;

        // TODO: Replace the fallback with a real ATR feed once historical bars are available
        // to the ticket. Until then use a conservative 1% of last price as the ATR estimate.
        var atr = Atr.HasValue && Atr > 0
            ? Atr.Value
            : Math.Max(0.01m, LastPrice.Value * 0.01m);

        return Math.Round(Math.Max(0.01m, LastPrice.Value - atr), 2, MidpointRounding.AwayFromZero);
    }

    [RelayCommand]
    private async Task SubmitAsync()
    {
        StatusMessage = "Submitting...";
        var account = await _accountCache.GetAsync(AccountId);
        var req = new OrderRequest
        {
            AccountId = AccountId,
            Symbol    = Symbol,
            AssetType = SymbolClassifier.AssetTypeFor(Symbol),
            Side      = Side,
            Quantity  = Quantity,
            OrderType = OrderType,
            LimitPrice = LimitPrice,
            StopPrice  = StopPrice,
            TimeInForce = TimeInForce,
            ExtendedHours = ExtendedHours,
            Session   = ExtendedHours ? OrderSession.Seamless : OrderSession.Normal,
            Source    = OrderSource.OrderTicket
        };

        var ticketQuote = LastPrice.HasValue ? new FastDOM.MarketData.Models.Quote { Symbol = Symbol, Last = LastPrice.Value } : null;
        var (success, msg) = await _orderService.SubmitOrderAsync(req, account, ticketQuote);
        StatusMessage = success ? $"Order accepted" : $"REJECTED: {msg}";
    }

    [RelayCommand]
    private void Clear()
    {
        LimitPrice = null;
        StopPrice  = null;
        StatusMessage = "";
    }
}
