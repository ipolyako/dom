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

    public List<OrderSide> OrderSides { get; } = [OrderSide.Buy, OrderSide.Sell];

    public List<OrderType> OrderTypes { get; } =
        [OrderType.Market, OrderType.Limit, OrderType.StopMarket, OrderType.StopLimit,
         OrderType.MarketableLimit, OrderType.Bracket];

    public List<TimeInForce> TifOptions { get; } = [TimeInForce.Day, TimeInForce.GTC, TimeInForce.IOC];

    public OrderTicketViewModel(ILogger<OrderTicketViewModel> logger,
        OrderService orderService, IBrokerClient broker)
    {
        _logger = logger;
        _orderService = orderService;
        _broker = broker;
    }

    partial void OnOrderTypeChanged(OrderType value)
    {
        if (LimitPrice == null && LastPrice.HasValue && NeedsLimitPrice(value))
            LimitPrice = LastPrice;
        if (IsStopType(value))
            AutoInferStopSide();
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
        LimitPrice = price;
        Side = side;
        OrderType = orderType;
        StatusMessage = $"Ready: {side} {orderType} @ {price:F2}";
    }

    [RelayCommand]
    private async Task SubmitAsync()
    {
        StatusMessage = "Submitting...";
        var account = await _broker.GetAccountSummaryAsync(AccountId);
        var req = new OrderRequest
        {
            AccountId = AccountId,
            Symbol    = Symbol,
            Side      = Side,
            Quantity  = Quantity,
            OrderType = OrderType,
            LimitPrice = LimitPrice,
            StopPrice  = StopPrice,
            TimeInForce = TimeInForce,
            ExtendedHours = ExtendedHours,
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
