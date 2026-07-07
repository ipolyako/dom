using FastDOM.Core.Models;
using FastDOM.MarketData.Models;

namespace FastDOM.Broker.Interfaces;

public interface IRiskManager
{
    RiskValidationResult ValidateOrder(OrderRequest request, AccountSummary account, Quote? quote);
    RiskValidationResult ValidateHotkeyAction(string actionType, AccountSummary account, Quote? quote);
    bool CanTrade(string accountId, string symbol);
    bool IsDailyLossLimitTriggered(string accountId);
    void RecordOrderSubmitted(OrderRequest request);
    void RecordOrderFilled(OrderState order, decimal fillPrice);
    void Reset();
}

public class RiskValidationResult
{
    public bool IsValid { get; init; }
    public string? RejectReason { get; init; }
    public RiskRejectCode RejectCode { get; init; }
    public bool RequiresConfirmation { get; init; }
    public string? ConfirmationMessage { get; init; }

    public static RiskValidationResult Valid(bool requiresConfirmation = false, string? confirmMsg = null) =>
        new() { IsValid = true, RequiresConfirmation = requiresConfirmation, ConfirmationMessage = confirmMsg };

    public static RiskValidationResult Reject(string reason, RiskRejectCode code = RiskRejectCode.Generic) =>
        new() { IsValid = false, RejectReason = reason, RejectCode = code };
}

public enum RiskRejectCode
{
    Generic,
    NoAccountSelected,
    LiveModeDisabled,
    AccountNotWhitelisted,
    SymbolNotAllowed,
    InvalidQuantity,
    InvalidPrice,
    TickMisaligned,
    NoValidQuote,
    MarketDataStale,
    ExceedsMaxShares,
    ExceedsMaxNotional,
    ExceedsMaxPositionNotional,
    DailyLossLimitTriggered,
    OrderRateLimitExceeded,
    ShortSaleNotAllowed,
    ExtendedHoursNotAllowed,
    OutsideTradingHours,
    SpreadTooWide,
    DuplicateOrderDetected,
    KillSwitchActive
}
