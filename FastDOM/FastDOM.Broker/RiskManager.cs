using FastDOM.Broker.Interfaces;
using FastDOM.Core.Enums;
using FastDOM.Core.Models;
using FastDOM.MarketData.Models;
using Microsoft.Extensions.Logging;

namespace FastDOM.Broker;

public class RiskManager : IRiskManager
{
    private readonly ILogger<RiskManager> _logger;
    private RiskProfile _profile;

    private bool _killSwitchActive;
    private readonly Dictionary<string, decimal> _dailyRealizedPnL = [];
    private readonly Dictionary<string, int> _dailyOrderCount = [];
    private readonly Queue<DateTime> _recentOrderTimes = [];

    public RiskManager(ILogger<RiskManager> logger, RiskProfile profile)
    {
        _logger = logger;
        _profile = profile;
    }

    public void UpdateProfile(RiskProfile profile) => _profile = profile;

    public bool CanTrade(string accountId, string symbol)
    {
        if (_killSwitchActive) return false;
        if (!_profile.LiveTradingEnabled) return true; // SIM mode always allowed

        if (_profile.SymbolBlacklist.Contains(symbol.ToUpperInvariant())) return false;

        return true;
    }

    public bool IsDailyLossLimitTriggered(string accountId)
    {
        if (!_dailyRealizedPnL.TryGetValue(accountId, out var pnl)) return false;
        return pnl <= -_profile.MaxDailyLoss;
    }

    public RiskValidationResult ValidateOrder(OrderRequest request, AccountSummary account, Quote? quote)
    {
        if (_killSwitchActive)
            return RiskValidationResult.Reject("Kill switch is active", RiskRejectCode.KillSwitchActive);

        if (string.IsNullOrEmpty(request.AccountId))
            return RiskValidationResult.Reject("No account selected", RiskRejectCode.NoAccountSelected);

        if (_profile.LiveTradingEnabled)
        {
        }

        var symbol = request.Symbol.ToUpperInvariant();

        if (_profile.SymbolBlacklist.Contains(symbol))
            return RiskValidationResult.Reject($"Symbol {symbol} is blacklisted", RiskRejectCode.SymbolNotAllowed);

        if (request.Quantity <= 0)
            return RiskValidationResult.Reject("Quantity must be > 0", RiskRejectCode.InvalidQuantity);

        if (_profile.MaxSharesPerOrder > 0 && request.Quantity > _profile.MaxSharesPerOrder)
            return RiskValidationResult.Reject(
                $"Quantity {request.Quantity} exceeds max {_profile.MaxSharesPerOrder}",
                RiskRejectCode.ExceedsMaxShares);

        if (quote != null)
        {
            if (quote.IsStale(_profile.MarketDataStaleMs) &&
                _profile.DisableOpeningOrdersWhenMarketDataStale)
                return RiskValidationResult.Reject(
                    $"Market data is stale ({quote.AgeMs}ms old, threshold {_profile.MarketDataStaleMs}ms)",
                    RiskRejectCode.MarketDataStale);

            if (_profile.MaxNotionalPerOrder > 0)
            {
                decimal refPrice = request.LimitPrice ?? quote.Last;
                decimal notional = refPrice * request.Quantity;
                if (notional > _profile.MaxNotionalPerOrder)
                    return RiskValidationResult.Reject(
                        $"Order notional ${notional:F0} exceeds max ${_profile.MaxNotionalPerOrder:F0}",
                        RiskRejectCode.ExceedsMaxNotional);
            }

            if (request.OrderType is OrderType.Market or OrderType.MarketableLimit)
            {
                decimal spread = quote.Spread;
                if (_profile.MaxSpreadForMarketOrders > 0 && spread > _profile.MaxSpreadForMarketOrders)
                    return RiskValidationResult.Reject(
                        $"Spread ${spread:F4} exceeds max ${_profile.MaxSpreadForMarketOrders:F4} for market orders",
                        RiskRejectCode.SpreadTooWide);
            }
        }

        if (!_profile.AllowShortSelling && request.Side == OrderSide.Sell)
        {
            if (account.Positions.TryGetValue(symbol, out var pos))
            {
                if (request.Quantity > pos.Quantity)
                    return RiskValidationResult.Reject("Short selling is not allowed", RiskRejectCode.ShortSaleNotAllowed);
            }
        }

        if (!_profile.AllowExtendedHours && request.ExtendedHours)
            return RiskValidationResult.Reject("Extended hours trading is not enabled", RiskRejectCode.ExtendedHoursNotAllowed);

        if (_profile.MaxDailyLoss > 0 && IsDailyLossLimitTriggered(request.AccountId))
            return RiskValidationResult.Reject(
                $"Daily loss limit of ${_profile.MaxDailyLoss} reached. Only closing orders allowed.",
                RiskRejectCode.DailyLossLimitTriggered);

        // Rate limiting
        CleanOldOrders();
        if (_profile.MaxOrdersPerMinute > 0 && _recentOrderTimes.Count >= _profile.MaxOrdersPerMinute)
            return RiskValidationResult.Reject(
                $"Order rate limit exceeded ({_profile.MaxOrdersPerMinute}/min)",
                RiskRejectCode.OrderRateLimitExceeded);

        // Confirmation threshold (0 = disabled)
        if (quote != null && (_profile.RequireConfirmationAboveNotional > 0 || _profile.RequireConfirmAllLiveOrders))
        {
            decimal refPrice = request.LimitPrice ?? quote.Last;
            decimal notional = refPrice * request.Quantity;
            if (_profile.RequireConfirmAllLiveOrders ||
                (_profile.RequireConfirmationAboveNotional > 0 && notional >= _profile.RequireConfirmationAboveNotional))
                return RiskValidationResult.Valid(
                    requiresConfirmation: true,
                    confirmMsg: $"Order notional ${notional:F0} requires confirmation");
        }

        return RiskValidationResult.Valid();
    }

    public RiskValidationResult ValidateHotkeyAction(string actionType, AccountSummary account, Quote? quote)
    {
        if (_killSwitchActive && actionType != "EmergencyFlattenCancel")
            return RiskValidationResult.Reject("Kill switch is active", RiskRejectCode.KillSwitchActive);

        if (_profile.MaxDailyLoss > 0 && IsDailyLossLimitTriggered(account.AccountId) &&
            !actionType.Contains("Flatten") && !actionType.Contains("Cancel") && !actionType.Contains("Close"))
            return RiskValidationResult.Reject("Daily loss limit triggered", RiskRejectCode.DailyLossLimitTriggered);

        return RiskValidationResult.Valid();
    }

    public void RecordOrderSubmitted(OrderRequest request)
    {
        _recentOrderTimes.Enqueue(DateTime.UtcNow);
        var key = request.AccountId;
        _dailyOrderCount[key] = _dailyOrderCount.GetValueOrDefault(key) + 1;
    }

    public void RecordOrderFilled(OrderState order, decimal fillPrice)
    {
        // Long (Buy) P&L: positive when exit price > entry price
        // Short (Sell) P&L: positive when exit price < entry price
        var pnl = order.Side == OrderSide.Buy
            ? (fillPrice - order.AverageFillPrice.GetValueOrDefault()) * order.QuantityFilled
            : -(fillPrice - order.AverageFillPrice.GetValueOrDefault()) * order.QuantityFilled;

        _dailyRealizedPnL[order.AccountId] =
            _dailyRealizedPnL.GetValueOrDefault(order.AccountId) + pnl;

        if (IsDailyLossLimitTriggered(order.AccountId))
            _logger.LogWarning("DAILY LOSS LIMIT TRIGGERED for account {AccountId}", order.AccountId);
    }

    public void ActivateKillSwitch()
    {
        _killSwitchActive = true;
        _logger.LogCritical("KILL SWITCH ACTIVATED");
    }

    public void DeactivateKillSwitch()
    {
        _killSwitchActive = false;
        _logger.LogWarning("Kill switch deactivated");
    }

    public bool IsKillSwitchActive => _killSwitchActive;

    public void Reset()
    {
        _killSwitchActive = false;
        _dailyRealizedPnL.Clear();
        _dailyOrderCount.Clear();
        _recentOrderTimes.Clear();
    }

    private void CleanOldOrders()
    {
        var cutoff = DateTime.UtcNow.AddMinutes(-1);
        while (_recentOrderTimes.TryPeek(out var oldest) && oldest < cutoff)
            _recentOrderTimes.Dequeue();
    }
}
