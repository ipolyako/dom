using FastDOM.Core.Enums;

namespace FastDOM.Core.Models;

public class OrderState
{
    public required string ClientOrderId { get; init; }
    public string? BrokerOrderId { get; set; }
    public required string AccountId { get; init; }
    public required string Symbol { get; init; }
    public required OrderSide Side { get; init; }
    public required int QuantityOrdered { get; init; }
    public int QuantityFilled { get; set; }
    public int QuantityRemaining => QuantityOrdered - QuantityFilled;
    public required OrderType OrderType { get; init; }
    public decimal? LimitPrice { get; set; }
    public decimal? StopPrice { get; set; }
    public decimal? AverageFillPrice { get; set; }
    public required OrderStatus Status { get; set; }
    public required OrderSource Source { get; init; }
    public DateTime CreatedAtUtc { get; init; } = DateTime.UtcNow;
    public DateTime LastUpdatedUtc { get; set; } = DateTime.UtcNow;
    public string? RejectReason { get; set; }
    public string? BrokerMessage { get; set; }
    public List<string> StatusHistory { get; } = [];

    public bool IsWorking => Status is OrderStatus.Working or OrderStatus.PartiallyFilled or OrderStatus.Accepted;
    public bool IsTerminal => Status is OrderStatus.Filled or OrderStatus.Cancelled or OrderStatus.RejectedLocally
                                        or OrderStatus.BrokerRejected or OrderStatus.Error or OrderStatus.Replaced;

    public void Transition(OrderStatus newStatus, string? reason = null)
    {
        StatusHistory.Add($"{LastUpdatedUtc:O} {Status} -> {newStatus}" + (reason != null ? $": {reason}" : ""));
        Status = newStatus;
        LastUpdatedUtc = DateTime.UtcNow;
        if (reason != null) RejectReason = reason;
    }
}
