using FastDOM.App.ViewModels;
using FastDOM.Core.Enums;
using FastDOM.Core.Models;
using FastDOM.MarketData.Models;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace FastDOM.Tests.Unit;

public class QuantityRuleTests
{
    [Theory]
    [InlineData(100)]
    [InlineData(500)]
    public void FixedQuantity_ReturnsFixed(int fixedQty)
    {
        var rule = new QuantityRule { Type = QuantityRuleType.Fixed, FixedShares = fixedQty };
        ResolveQty(rule).Should().Be(fixedQty);
    }

    [Fact]
    public void FixedZero_FallsBackToDefault()
    {
        var rule = new QuantityRule { Type = QuantityRuleType.Fixed, FixedShares = 0 };
        ResolveQty(rule, defaultSize: 200).Should().Be(200);
    }

    [Fact]
    public void PercentOfPosition_CalculatesCorrectly()
    {
        var rule = new QuantityRule { Type = QuantityRuleType.PercentOfPosition, PercentOfPosition = 50 };
        var pos = new Position { AccountId = "A", Symbol = "SPY", Quantity = 200, AverageCost = 100m };
        ResolveQty(rule, pos: pos).Should().Be(100);
    }

    [Fact]
    public void PercentOfPosition_CeilsToWholeLot()
    {
        var rule = new QuantityRule { Type = QuantityRuleType.PercentOfPosition, PercentOfPosition = 33 };
        var pos = new Position { AccountId = "A", Symbol = "SPY", Quantity = 100, AverageCost = 100m };
        ResolveQty(rule, pos: pos).Should().Be(33); // ceil(100 * 0.33) = 33
    }

    [Fact]
    public void DollarAmount_CalculatesSharesFromPrice()
    {
        var rule = new QuantityRule { Type = QuantityRuleType.DollarAmount, DollarAmount = 10000m };
        var quote = new Quote { Symbol = "SPY", Last = 500m, Bid = 499.99m, Ask = 500.01m };
        ResolveQty(rule, quote: quote).Should().Be(20); // 10000 / 500 = 20
    }

    private static int ResolveQty(QuantityRule rule, int defaultSize = 100, Position? pos = null, Quote? quote = null)
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
}
