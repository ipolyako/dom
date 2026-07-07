using CommunityToolkit.Mvvm.ComponentModel;
using FastDOM.App.Services;
using FastDOM.Broker.Interfaces;
using FastDOM.Core.Enums;
using FastDOM.Core.Models;
using FastDOM.Infrastructure.Config;
using FastDOM.MarketData.Models;
using Microsoft.Extensions.Logging;

namespace FastDOM.App.ViewModels;

public partial class HotButtonsViewModel : ObservableObject
{
    private readonly ILogger<HotButtonsViewModel> _logger;
    private readonly OrderService _orderService;
    private readonly IBrokerClient _broker;
    private readonly IRiskManager _risk;
    private readonly ConfigManager _config;

    public List<HotButtonConfig> Buttons => _config.HotButtons;

    public event Action<string>? ToastRequested;

    public HotButtonsViewModel(ILogger<HotButtonsViewModel> logger,
        OrderService orderService, IBrokerClient broker,
        IRiskManager risk, ConfigManager config)
    {
        _logger = logger;
        _orderService = orderService;
        _broker = broker;
        _risk = risk;
        _config = config;
    }

    public async Task ExecuteButtonAsync(HotButtonConfig btn, string symbol, string accountId,
        int defaultSize, Quote? quote, Position? position)
    {
        if (!btn.IsEnabled) return;
        _logger.LogInformation("Hot button: {Label} ({Action})", btn.Label, btn.Action);
        await ExecuteActionInternalAsync(btn.Action, symbol, accountId,
            ResolveQuantity(btn.QuantityRule, defaultSize, position, quote),
            ResolvePrice(btn.PriceRule, quote, position),
            btn.OrderType, quote, position);
    }

    public async Task ExecuteActionAsync(string actionType, string symbol, string accountId, int defaultSize)
    {
        _logger.LogInformation("Execute action: {Action}", actionType);
        if (!Enum.TryParse<HotButtonAction>(actionType, out var action)) return;
        await ExecuteActionInternalAsync(action, symbol, accountId, defaultSize, null,
            OrderType.MarketableLimit, null, null);
    }

    private async Task ExecuteActionInternalAsync(
        HotButtonAction action, string symbol, string accountId,
        int qty, decimal? price, OrderType orderType, Quote? quote, Position? position)
    {
        switch (action)
        {
            case HotButtonAction.BuyMarket:
                await PlaceAsync(accountId, symbol, OrderSide.Buy, qty, OrderType.Market, null);
                break;
            case HotButtonAction.SellMarket:
                await PlaceAsync(accountId, symbol, OrderSide.Sell, qty, OrderType.Market, null);
                break;
            case HotButtonAction.BuyAsk:
                await PlaceAsync(accountId, symbol, OrderSide.Buy, qty, OrderType.Limit, quote?.Ask);
                break;
            case HotButtonAction.SellBid:
                await PlaceAsync(accountId, symbol, OrderSide.Sell, qty, OrderType.Limit, quote?.Bid);
                break;
            case HotButtonAction.BuyBid:
                await PlaceAsync(accountId, symbol, OrderSide.Buy, qty, OrderType.Limit, quote?.Bid);
                break;
            case HotButtonAction.SellAsk:
                await PlaceAsync(accountId, symbol, OrderSide.Sell, qty, OrderType.Limit, quote?.Ask);
                break;
            case HotButtonAction.Flatten:
                await FlattenAsync(accountId, symbol, position);
                break;
            case HotButtonAction.Reverse:
                await ReverseAsync(accountId, symbol, position, quote);
                break;
            case HotButtonAction.CancelSymbol:
                await _orderService.CancelAllForSymbolAsync(accountId, symbol);
                ToastRequested?.Invoke($"Cancelled all {symbol} orders");
                break;
            case HotButtonAction.CancelAll:
                var allSymbols = _orderService.ActiveOrders.Values
                    .Where(o => o.IsWorking && o.AccountId == accountId)
                    .Select(o => o.Symbol).Distinct().ToList();
                foreach (var sym in allSymbols)
                    await _orderService.CancelAllForSymbolAsync(accountId, sym);
                ToastRequested?.Invoke($"Cancelled all orders ({allSymbols.Count} symbols)");
                break;
            case HotButtonAction.SellPercent when position != null:
                var sellQty = (int)Math.Ceiling(Math.Abs(position.Quantity) *
                              (qty / 100.0));
                if (sellQty > 0)
                    await PlaceAsync(accountId, symbol, OrderSide.Sell, sellQty, OrderType.Market, null);
                break;
            case HotButtonAction.MoveStopToBreakeven:
                ToastRequested?.Invoke("Move Stop to BE: select the stop order first");
                break;
            default:
                _logger.LogWarning("Unhandled hot button action: {Action}", action);
                break;
        }
    }

    private async Task PlaceAsync(string accountId, string symbol, OrderSide side,
        int qty, OrderType orderType, decimal? price)
    {
        if (qty <= 0) { ToastRequested?.Invoke("Qty = 0, order not placed"); return; }

        var account = await _broker.GetAccountSummaryAsync(accountId);
        var req = new OrderRequest
        {
            AccountId = accountId,
            Symbol    = symbol,
            Side      = side,
            Quantity  = qty,
            OrderType = orderType,
            LimitPrice = price,
            Source    = OrderSource.HotButton
        };

        var (success, msg) = await _orderService.SubmitOrderAsync(req, account, null);
        ToastRequested?.Invoke(success ? $"Order sent: {side} {qty} {symbol}" : $"REJECTED: {msg}");
    }

    private async Task FlattenAsync(string accountId, string symbol, Position? position)
    {
        if (position == null || position.IsFlat)
        {
            ToastRequested?.Invoke("No position to flatten");
            return;
        }
        var side = position.Side == PositionSide.Long ? OrderSide.Sell : OrderSide.Buy;
        await _orderService.CancelAllForSymbolAsync(accountId, symbol);
        await PlaceAsync(accountId, symbol, side, Math.Abs(position.Quantity), OrderType.Market, null);
    }

    private async Task ReverseAsync(string accountId, string symbol, Position? position, Quote? quote)
    {
        if (position == null || position.IsFlat)
        {
            ToastRequested?.Invoke("No position to reverse");
            return;
        }
        var side = position.Side == PositionSide.Long ? OrderSide.Sell : OrderSide.Buy;
        var qty = Math.Abs(position.Quantity) * 2;
        await _orderService.CancelAllForSymbolAsync(accountId, symbol);
        await PlaceAsync(accountId, symbol, side, qty, OrderType.Market, null);
    }

    private static int ResolveQuantity(QuantityRule rule, int defaultSize, Position? pos, Quote? quote)
    {
        return rule.Type switch
        {
            QuantityRuleType.Fixed => rule.FixedShares > 0 ? rule.FixedShares : defaultSize,
            QuantityRuleType.PercentOfPosition when pos != null && !pos.IsFlat =>
                (int)Math.Ceiling(Math.Abs(pos.Quantity) * rule.PercentOfPosition / 100.0m),
            QuantityRuleType.DollarAmount when quote != null && quote.Last > 0 =>
                (int)(rule.DollarAmount / quote.Last),
            _ => defaultSize
        };
    }

    private static decimal? ResolvePrice(PriceRule rule, Quote? quote, Position? pos) =>
        rule.Type switch
        {
            PriceRuleType.Bid => quote?.Bid,
            PriceRuleType.Ask => quote?.Ask,
            PriceRuleType.Last => quote?.Last,
            PriceRuleType.Mid => quote?.Mid,
            PriceRuleType.AverageCost => pos?.AverageCost,
            PriceRuleType.ManualPrice => rule.ManualPrice > 0 ? rule.ManualPrice : null,
            _ => null
        };
}
