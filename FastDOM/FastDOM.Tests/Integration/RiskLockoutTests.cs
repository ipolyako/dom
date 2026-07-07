using FastDOM.Broker;
using FastDOM.Broker.Interfaces;
using FastDOM.Core.Enums;
using FastDOM.Core.Models;
using FastDOM.MarketData.Models;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace FastDOM.Tests.Integration;

public class RiskLockoutTests
{
    private static RiskManager BuildRisk(decimal maxDailyLoss = 100m) =>
        new(NullLogger<RiskManager>.Instance, new RiskProfile
        {
            MaxDailyLoss = maxDailyLoss,
            MaxSharesPerOrder = 1000,
            MaxNotionalPerOrder = 1_000_000m,
            DisableOpeningOrdersWhenMarketDataStale = false
        });

    private static AccountSummary Account(string id = "ACC1") =>
        new() { AccountId = id, AccountName = id, BuyingPower = 100_000m };

    [Fact]
    public void DailyLossLimit_BlocksNewOrders()
    {
        var risk = BuildRisk(maxDailyLoss: 50m);

        // Simulate a $60 loss
        var fillState = new OrderState
        {
            ClientOrderId = "X", AccountId = "ACC1", Symbol = "SPY",
            Side = OrderSide.Buy, QuantityOrdered = 10, QuantityFilled = 10,
            OrderType = OrderType.Limit, Status = OrderStatus.Filled,
            Source = OrderSource.DomClick, AverageFillPrice = 100m
        };
        risk.RecordOrderFilled(fillState, 94m); // bought 100, now 94 → -$60

        risk.IsDailyLossLimitTriggered("ACC1").Should().BeTrue();

        var req = new OrderRequest
        {
            AccountId = "ACC1", Symbol = "SPY", Side = OrderSide.Buy, Quantity = 10,
            OrderType = OrderType.Limit, LimitPrice = 94m, Source = OrderSource.DomClick
        };
        var result = risk.ValidateOrder(req, Account(), null);
        result.IsValid.Should().BeFalse();
        result.RejectCode.Should().Be(RiskRejectCode.DailyLossLimitTriggered);
    }

    [Fact]
    public void RateLimitExceeded_Blocked()
    {
        var risk = new RiskManager(NullLogger<RiskManager>.Instance, new RiskProfile
        {
            MaxOrdersPerMinute = 3,
            MaxSharesPerOrder = 1000,
            MaxNotionalPerOrder = 1_000_000m,
            DisableOpeningOrdersWhenMarketDataStale = false
        });

        var req = new OrderRequest
        {
            AccountId = "ACC1", Symbol = "SPY", Side = OrderSide.Buy, Quantity = 1,
            OrderType = OrderType.Market, Source = OrderSource.Hotkey
        };

        for (int i = 0; i < 3; i++)
            risk.RecordOrderSubmitted(req);

        var result = risk.ValidateOrder(req, Account(), null);
        result.IsValid.Should().BeFalse();
        result.RejectCode.Should().Be(RiskRejectCode.OrderRateLimitExceeded);
    }

    [Fact]
    public void Reset_ClearsAllLimits()
    {
        var risk = BuildRisk(maxDailyLoss: 10m);
        var fillState = new OrderState
        {
            ClientOrderId = "X", AccountId = "ACC1", Symbol = "SPY",
            Side = OrderSide.Buy, QuantityOrdered = 10, QuantityFilled = 10,
            OrderType = OrderType.Limit, Status = OrderStatus.Filled,
            Source = OrderSource.DomClick, AverageFillPrice = 100m
        };
        risk.RecordOrderFilled(fillState, 90m);
        risk.IsDailyLossLimitTriggered("ACC1").Should().BeTrue();

        risk.Reset();
        risk.IsDailyLossLimitTriggered("ACC1").Should().BeFalse();
    }
}
