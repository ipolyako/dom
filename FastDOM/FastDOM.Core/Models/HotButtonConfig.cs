using FastDOM.Core.Enums;

namespace FastDOM.Core.Models;

public class HotButtonConfig
{
    public required string Id { get; init; }
    public required string Label { get; set; }
    public string Color { get; set; } = "#2196F3";
    public string TextColor { get; set; } = "#FFFFFF";
    // Script takes precedence over Action when set.
    public string? Script { get; set; }
    public required HotButtonAction Action { get; set; }
    public QuantityRule QuantityRule { get; set; } = new();
    public PriceRule PriceRule { get; set; } = new();
    public OrderType OrderType { get; set; } = OrderType.Limit;
    public TimeInForce TimeInForce { get; set; } = TimeInForce.Day;
    public bool RequireConfirmation { get; set; } = false;
    public bool IsEnabled { get; set; } = true;
    public string? KeyboardShortcut { get; set; }
    public int DisplayOrder { get; set; }
}

public enum HotButtonAction
{
    BuyMarket, SellMarket,
    BuyAsk, SellBid, BuyBid, SellAsk,
    BuyLimit, SellLimit,
    BuyMarketableLimit, SellMarketableLimit,
    Flatten, Reverse,
    CancelAll, CancelBuys, CancelSells, CancelSymbol,
    SetStop, SetTarget, MoveStopToBreakeven,
    SellPercent, CoverPercent,
    SellShares, CoverShares,
    AddToPosition, ScaleOut,
    Custom
}

public class QuantityRule
{
    public QuantityRuleType Type { get; set; } = QuantityRuleType.Fixed;
    public int FixedShares { get; set; } = 0; // 0 = use global ShareSize
    public decimal DollarAmount { get; set; }
    public decimal PercentOfPosition { get; set; }
    public decimal PercentOfBuyingPower { get; set; }
    public decimal StopDistanceForRisk { get; set; }
    public decimal RiskDollars { get; set; }
    public bool RoundToLotSize { get; set; } = true;
}

public enum QuantityRuleType
{
    Fixed,
    DollarAmount,
    PercentOfPosition,
    PercentOfBuyingPower,
    RiskBased
}

public class PriceRule
{
    public PriceRuleType Type { get; set; } = PriceRuleType.DomClickedPrice;
    public decimal Offset { get; set; }
    public decimal ManualPrice { get; set; }
}

public enum PriceRuleType
{
    Bid, Ask, Last, Mid,
    DomClickedPrice,
    OffsetFromBid, OffsetFromAsk, OffsetFromLast,
    AverageCost,
    ManualPrice
}
