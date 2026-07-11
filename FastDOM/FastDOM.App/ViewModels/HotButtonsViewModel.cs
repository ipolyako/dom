using System.Collections.ObjectModel;
using System.Text.RegularExpressions;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using FastDOM.App.Services;
using FastDOM.App.Views;
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
    private readonly AccountSummaryCache _accountCache;
    private readonly IRiskManager _risk;
    private readonly ConfigManager _config;
    private readonly ScriptEngine _scriptEngine;
    private readonly HashSet<string> _flattenInFlight = new(StringComparer.OrdinalIgnoreCase);

    // ObservableCollection so the ItemsControl reacts to individual Add/Remove after save.
    public ObservableCollection<HotButtonConfig> Buttons { get; } = [];
    public ObservableCollection<HotButtonConfig> EntryButtons { get; } = [];
    public ObservableCollection<HotButtonConfig> FullWidthButtons { get; } = [];
    public ObservableCollection<HotButtonConfig> StrategyButtons { get; } = [];
    [ObservableProperty] private decimal _riskAmount = 250m;
    [ObservableProperty] private decimal _tradeAmount = 10000m;

    public void RefreshButtons()
    {
        Buttons.Clear();
        EntryButtons.Clear();
        FullWidthButtons.Clear();
        StrategyButtons.Clear();

        foreach (var b in _config.HotButtons.OrderBy(b => b.DisplayOrder))
        {
            Buttons.Add(b);
            if (IsFullWidthActionButton(b))
                FullWidthButtons.Add(b);
            else if (b.DisplayOrder <= 5)
                EntryButtons.Add(b);
            else
                StrategyButtons.Add(b);
        }
    }

    private static bool IsFullWidthActionButton(HotButtonConfig button) =>
        string.Equals(button.Id, "flatten", StringComparison.OrdinalIgnoreCase)
        || string.Equals(button.Id, "cancel_sym", StringComparison.OrdinalIgnoreCase)
        || button.Action is HotButtonAction.Flatten or HotButtonAction.CancelSymbol;

    public event Action<string>? ToastRequested;

    public HotButtonsViewModel(ILogger<HotButtonsViewModel> logger,
        OrderService orderService, IBrokerClient broker,
        IRiskManager risk, ConfigManager config, ScriptEngine scriptEngine,
        AccountSummaryCache accountCache)
    {
        _logger = logger;
        _orderService = orderService;
        _broker = broker;
        _risk = risk;
        _config = config;
        _scriptEngine = scriptEngine;
        _accountCache = accountCache;
        RefreshButtons();
    }

    public async Task ExecuteButtonAsync(HotButtonConfig btn, string symbol, string accountId,
        int defaultSize, Quote? quote, Position? position,
        IReadOnlyDictionary<string, decimal>? presetVariables = null)
    {
        if (!btn.IsEnabled) return;
        AccountSummary? account = null;
        position = await ResolvePositionForSymbolAsync(accountId, symbol, position);

        if (IsSecureButton(btn))
        {
            var livePosition = position;
            if (livePosition == null || livePosition.IsFlat)
                return;
        }

        if (!string.IsNullOrWhiteSpace(btn.Script))
        {
            _logger.LogInformation("Hot button script: {Label}", btn.Label);
            account ??= await _accountCache.GetAsync(accountId);
            var script = ApplyRiskAmountOverride(btn, btn.Script);
            var ctx = new ScriptContext
            {
                Symbol      = symbol,
                AccountId   = accountId,
                DefaultSize = defaultSize,
                Quote       = quote,
                Position    = position,
                Account     = account,
                Orders      = _orderService,
                Broker      = _broker,
                Toast       = msg => ToastRequested?.Invoke(msg),
                PromptUser  = ShowInputDialogAsync,
            };
            ctx.Variables["AMOUNT"] = TradeAmount;
            if (presetVariables != null)
                foreach (var variable in presetVariables)
                    ctx.Variables[variable.Key.ToUpperInvariant()] = variable.Value;
            await _scriptEngine.ExecuteAsync(script, ctx);
            return;
        }

        _logger.LogInformation("Hot button: {Label} ({Action})", btn.Label, btn.Action);
        await ExecuteActionInternalAsync(btn.Action, symbol, accountId,
            ResolveQuantity(btn.QuantityRule, defaultSize, position, quote),
            ResolvePrice(btn.PriceRule, quote, position),
            btn.OrderType, quote, position);
    }

    private string ApplyRiskAmountOverride(HotButtonConfig btn, string script)
    {
        if (RiskAmount <= 0) return script;
        var isRiskBuy = string.Equals(btn.Id, "risk_buy_5t", StringComparison.OrdinalIgnoreCase)
            || string.Equals(btn.Id, "risk_buy_simple", StringComparison.OrdinalIgnoreCase)
            || string.Equals(btn.Label, "Risk Buy", StringComparison.OrdinalIgnoreCase)
            || string.Equals(btn.Label, "Risk Buy Simple", StringComparison.OrdinalIgnoreCase);
        return isRiskBuy
            ? Regex.Replace(script, @"RISK:\$\d+(\.\d+)?", $"RISK:${RiskAmount:0.##}", RegexOptions.IgnoreCase)
            : script;
    }

    private static bool IsSecureButton(HotButtonConfig btn) =>
        string.Equals(btn.Id, "secure_position", StringComparison.OrdinalIgnoreCase)
        || string.Equals(btn.Label, "Secure", StringComparison.OrdinalIgnoreCase);

    // Maps hotkey ActionType strings → HotButtonAction (shared with HotkeyService.ActionTypeMap).
    private static readonly IReadOnlyDictionary<string, HotButtonAction> _hotkeyMap =
        HotkeyService.ActionTypeMap;

    public async Task ExecuteActionAsync(string actionType, string symbol, string accountId,
        int defaultSize, Quote? quote = null, Position? position = null)
    {
        _logger.LogInformation("Execute action: {Action}", actionType);
        if (!Enum.TryParse<HotButtonAction>(actionType, out var action) &&
            !_hotkeyMap.TryGetValue(actionType, out action))
        {
            _logger.LogWarning("Unknown hotkey action: {Action}", actionType);
            return;
        }
        await ExecuteActionInternalAsync(action, symbol, accountId, defaultSize, null,
            OrderType.MarketableLimit, quote, position);
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
            case HotButtonAction.BuyMarketableLimit:
            {
                var p = quote?.Ask > 0 ? quote.Ask : (decimal?)null;
                await PlaceAsync(accountId, symbol, OrderSide.Buy, qty,
                    p.HasValue ? OrderType.MarketableLimit : OrderType.Market, p);
                break;
            }
            case HotButtonAction.SellMarketableLimit:
            {
                var p = quote?.Bid > 0 ? quote.Bid : (decimal?)null;
                await PlaceAsync(accountId, symbol, OrderSide.Sell, qty,
                    p.HasValue ? OrderType.MarketableLimit : OrderType.Market, p);
                break;
            }
            case HotButtonAction.BuyBid:
                await PlaceAsync(accountId, symbol, OrderSide.Buy, qty, OrderType.Limit, quote?.Bid);
                break;
            case HotButtonAction.SellAsk:
                await PlaceAsync(accountId, symbol, OrderSide.Sell, qty, OrderType.Limit, quote?.Ask);
                break;
            case HotButtonAction.Flatten:
                await FlattenAsync(accountId, symbol, position, quote);
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
                // qty is already resolved to shares by ResolveQuantity — use it directly
                if (qty > 0)
                    await PlaceAsync(accountId, symbol, OrderSide.Sell, qty, OrderType.Market, null);
                break;
            case HotButtonAction.MoveStopToBreakeven:
                ToastRequested?.Invoke("Move Stop to BE: select the stop order first");
                break;
            default:
                _logger.LogWarning("Unhandled hot button action: {Action}", action);
                break;
        }
    }

    // Shows an InputDialog on the UI thread from any calling thread.
    private static async Task<decimal?> ShowInputDialogAsync(string varName, string prompt)
    {
        decimal? result = null;
        await Application.Current.Dispatcher.InvokeAsync(() =>
        {
            var dlg = new InputDialog(prompt, "FastDOM — Enter Price")
            {
                Owner = Application.Current.MainWindow
            };
            if (dlg.ShowDialog() == true)
                result = dlg.Value;
        });
        return result;
    }

    private async Task PlaceAsync(string accountId, string symbol, OrderSide side,
        int qty, OrderType orderType, decimal? price)
    {
        if (qty <= 0) { ToastRequested?.Invoke("Qty = 0, order not placed"); return; }

        var account = await _accountCache.GetAsync(accountId);
        var isExt = IsOutsideRegularHours(DateTime.UtcNow);
        var req = new OrderRequest
        {
            AccountId = accountId,
            Symbol    = symbol,
            AssetType = SymbolClassifier.AssetTypeFor(symbol),
            Side      = side,
            Quantity  = qty,
            OrderType = orderType,
            LimitPrice = price,
            ExtendedHours = isExt,
            Session   = isExt ? OrderSession.Seamless : OrderSession.Normal,
            Source    = OrderSource.HotButton
        };

        var (success, msg) = await _orderService.SubmitOrderAsync(req, account, null);
        ToastRequested?.Invoke(success ? $"Order sent: {side} {qty} {symbol}" : $"REJECTED: {msg}");
    }

    private static bool IsOutsideRegularHours(DateTime utcNow)
    {
        var etZone = TimeZoneInfo.FindSystemTimeZoneById("Eastern Standard Time");
        var et = TimeZoneInfo.ConvertTimeFromUtc(utcNow, etZone);
        if (et.DayOfWeek is DayOfWeek.Saturday or DayOfWeek.Sunday) return true;
        var t = et.TimeOfDay;
        return t < new TimeSpan(9, 30, 0) || t >= new TimeSpan(16, 0, 0);
    }

    private async Task FlattenAsync(string accountId, string symbol, Position? position, Quote? quote)
    {
        var key = $"{accountId}|{symbol.Trim().ToUpperInvariant()}";
        lock (_flattenInFlight)
        {
            if (!_flattenInFlight.Add(key))
            {
                ToastRequested?.Invoke($"Flatten already pending for {symbol}");
                return;
            }
        }

        var releaseInBackground = false;
        try
        {
            position = await ResolvePositionForSymbolAsync(accountId, symbol, position);
            if (position == null || position.IsFlat)
            {
                ToastRequested?.Invoke($"No {symbol} position to flatten");
                return;
            }
            var side = position.Side == PositionSide.Long ? OrderSide.Sell : OrderSide.Buy;
            var (orderType, price) = ResolveFlattenOrder(side, quote);
            if (orderType == OrderType.Limit && price == null)
            {
                ToastRequested?.Invoke($"No {(side == OrderSide.Sell ? "bid" : "ask")} quote for after-hours flatten");
                return;
            }
            await _orderService.CancelAllForSymbolFastAsync(accountId, position.Symbol);
            await PlaceAsync(accountId, position.Symbol, side, Math.Abs(position.Quantity), orderType, price);

            releaseInBackground = true;
            _ = RefreshAfterFlattenAsync(accountId, symbol, key);
        }
        finally
        {
            if (!releaseInBackground)
            {
                lock (_flattenInFlight)
                    _flattenInFlight.Remove(key);
            }
        }
    }

    private async Task RefreshAfterFlattenAsync(string accountId, string symbol, string flattenKey)
    {
        try
        {
            await Task.Delay(2500);
            _accountCache.Invalidate(accountId);
            await _orderService.SyncOrdersAsync(accountId);
            await _accountCache.RefreshAsync(accountId);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Post-flatten account refresh failed for {Account}/{Symbol}", accountId, symbol);
        }
        finally
        {
            lock (_flattenInFlight)
                _flattenInFlight.Remove(flattenKey);
        }
    }

    private static (OrderType OrderType, decimal? Price) ResolveFlattenOrder(OrderSide side, Quote? quote)
    {
        if (!IsOutsideRegularHours(DateTime.UtcNow))
            return (OrderType.Market, null);

        decimal? price = side == OrderSide.Sell
            ? quote?.Bid > 0 ? quote.Bid : null
            : quote?.Ask > 0 ? quote.Ask : null;

        return (OrderType.Limit, price);
    }

    private async Task ReverseAsync(string accountId, string symbol, Position? position, Quote? quote)
    {
        position = await ResolvePositionForSymbolAsync(accountId, symbol, position);
        if (position == null || position.IsFlat)
        {
            ToastRequested?.Invoke($"No {symbol} position to reverse");
            return;
        }
        var side = position.Side == PositionSide.Long ? OrderSide.Sell : OrderSide.Buy;
        var qty = Math.Abs(position.Quantity) * 2;
        await _orderService.CancelAllForSymbolAsync(accountId, position.Symbol);
        await PlaceAsync(accountId, position.Symbol, side, qty, OrderType.Market, null);
    }

    private async Task<Position?> ResolvePositionForSymbolAsync(string accountId, string symbol, Position? candidate)
    {
        var normalized = symbol.Trim().ToUpperInvariant();
        if (candidate != null &&
            string.Equals(candidate.Symbol, normalized, StringComparison.OrdinalIgnoreCase))
            return candidate;

        if (candidate != null)
        {
            _logger.LogWarning(
                "Ignoring stale position {PositionSymbol} while executing action for {Symbol}",
                candidate.Symbol,
                normalized);
        }

        var summary = await _accountCache.GetAsync(accountId);
        summary.Positions.TryGetValue(normalized, out var position);
        return position;
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
