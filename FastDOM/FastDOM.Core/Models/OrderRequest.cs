using FastDOM.Core.Enums;

namespace FastDOM.Core.Models;

public class OrderRequest
{
    public string ClientOrderId { get; init; } = Guid.NewGuid().ToString("N");
    public required string AccountId { get; init; }
    public required string Symbol { get; init; }
    public AssetType AssetType { get; init; } = AssetType.Equity;
    public required OrderSide Side { get; init; }
    public required int Quantity { get; init; }
    public required OrderType OrderType { get; init; }
    public decimal? LimitPrice { get; init; }
    public decimal? StopPrice { get; init; }
    public decimal? StopLimitPrice { get; init; }
    public TimeInForce TimeInForce { get; init; } = TimeInForce.Day;
    public OrderSession Session { get; init; } = OrderSession.Normal;
    public bool ExtendedHours { get; init; }
    public BracketConfig? Bracket { get; init; }
    public required OrderSource Source { get; init; }
    public DateTime CreatedAtUtc { get; init; } = DateTime.UtcNow;
    public Dictionary<string, string> RiskMetadata { get; init; } = [];
}

public class BracketConfig
{
    public decimal? StopLossPrice { get; init; }
    public decimal? StopLossOffset { get; init; }
    public decimal? ProfitTargetPrice { get; init; }
    public decimal? ProfitTargetOffset { get; init; }
}

public class OrderReplace
{
    public required string OriginalClientOrderId { get; init; }
    public required string BrokerOrderId { get; init; }
    public decimal? NewLimitPrice { get; init; }
    public decimal? NewStopPrice { get; init; }
    public int? NewQuantity { get; init; }
    public required OrderSource Source { get; init; }
}
