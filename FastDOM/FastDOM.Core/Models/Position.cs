using FastDOM.Core.Enums;

namespace FastDOM.Core.Models;

public class Position
{
    public required string AccountId { get; init; }
    public required string Symbol { get; init; }
    public int Quantity { get; set; }
    public decimal AverageCost { get; set; }
    public decimal? UnrealizedPnL { get; set; }
    public decimal? DayPnL { get; set; }
    public decimal? RealizedPnL { get; set; }
    public decimal? CurrentPrice { get; set; }
    public DateTime LastUpdatedUtc { get; set; } = DateTime.UtcNow;

    public PositionSide Side => Quantity switch
    {
        > 0 => PositionSide.Long,
        < 0 => PositionSide.Short,
        _ => PositionSide.Flat
    };

    public bool IsFlat => Quantity == 0;
    public decimal Notional => Math.Abs(Quantity) * (CurrentPrice ?? AverageCost);

    public decimal? BreakevenPrice => IsFlat ? null : AverageCost;
}

public class AccountSummary
{
    public required string AccountId { get; init; }
    public required string AccountName { get; init; }
    public decimal? BuyingPower { get; set; }
    public decimal? NetLiquidation { get; set; }
    public decimal? DayTradingBuyingPower { get; set; }
    public decimal? DailyRealizedPnL { get; set; }
    public decimal? DailyUnrealizedPnL { get; set; }
    public DateTime LastUpdatedUtc { get; set; } = DateTime.UtcNow;
    public Dictionary<string, Position> Positions { get; set; } = [];
}
