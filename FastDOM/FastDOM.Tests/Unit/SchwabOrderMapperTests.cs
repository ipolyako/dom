using System.Text.Json;
using FastDOM.Broker.Schwab.Mapping;
using FastDOM.Core.Enums;
using FastDOM.Core.Models;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace FastDOM.Tests.Unit;

public class SchwabOrderMapperTests
{
    private static SchwabOrderMapper Mapper() => new(NullLogger<SchwabOrderMapper>.Instance);

    private static OrderRequest BaseReq(OrderType type, OrderSide side = OrderSide.Buy,
        decimal? limit = null, decimal? stop = null, int qty = 10) =>
        new()
        {
            AccountId = "ACC1", Symbol = "SPY", Side = side, Quantity = qty,
            OrderType = type, LimitPrice = limit, StopPrice = stop,
            Source = OrderSource.DomClick
        };

    [Fact]
    public void MarketOrder_HasCorrectStructure()
    {
        var json = Mapper().MapToJson(BaseReq(OrderType.Market));
        using var doc = JsonDocument.Parse(json);
        doc.RootElement.GetProperty("orderType").GetString().Should().Be("MARKET");
        doc.RootElement.GetProperty("orderStrategyType").GetString().Should().Be("SINGLE");
        doc.RootElement.GetProperty("orderLegCollection")[0].GetProperty("instruction").GetString().Should().Be("BUY");
        doc.RootElement.GetProperty("orderLegCollection")[0].GetProperty("quantity").GetDecimal().Should().Be(10);
    }

    [Fact]
    public void LimitOrder_HasPriceField()
    {
        var json = Mapper().MapToJson(BaseReq(OrderType.Limit, limit: 100.50m));
        using var doc = JsonDocument.Parse(json);
        doc.RootElement.GetProperty("orderType").GetString().Should().Be("LIMIT");
        doc.RootElement.GetProperty("price").GetString().Should().Be("100.50");
    }

    [Fact]
    public void StopMarketOrder_HasStopPrice()
    {
        var json = Mapper().MapToJson(BaseReq(OrderType.StopMarket, side: OrderSide.Sell, stop: 99.50m));
        using var doc = JsonDocument.Parse(json);
        doc.RootElement.GetProperty("orderType").GetString().Should().Be("STOP");
        doc.RootElement.GetProperty("stopPrice").GetString().Should().Be("99.50");
        doc.RootElement.GetProperty("orderLegCollection")[0].GetProperty("instruction").GetString().Should().Be("SELL");
    }

    [Fact]
    public void StopLimitOrder_HasBothPrices()
    {
        var json = Mapper().MapToJson(BaseReq(OrderType.StopLimit, stop: 99.50m, limit: 99.25m));
        using var doc = JsonDocument.Parse(json);
        doc.RootElement.GetProperty("orderType").GetString().Should().Be("STOP_LIMIT");
        doc.RootElement.GetProperty("stopPrice").GetString().Should().Be("99.50");
        doc.RootElement.GetProperty("price").GetString().Should().Be("99.25");
    }

    [Fact]
    public void BracketOrder_HasTriggerStrategyWithOcoChild()
    {
        var req = new OrderRequest
        {
            AccountId = "ACC1", Symbol = "SPY", Side = OrderSide.Buy, Quantity = 10,
            OrderType = OrderType.Bracket, LimitPrice = 100m, Source = OrderSource.DomClick,
            Bracket = new BracketConfig { ProfitTargetPrice = 105m, StopLossPrice = 95m }
        };
        var json = Mapper().MapToJson(req);
        using var doc = JsonDocument.Parse(json);
        doc.RootElement.GetProperty("orderStrategyType").GetString().Should().Be("TRIGGER");
        var children = doc.RootElement.GetProperty("childOrderStrategies");
        children.GetArrayLength().Should().Be(1);
        children[0].GetProperty("orderStrategyType").GetString().Should().Be("OCO");
    }

    [Fact]
    public void UnsupportedOrderType_Throws()
    {
        var req = BaseReq(OrderType.OSO);
        var act = () => Mapper().MapToJson(req);
        act.Should().Throw<NotSupportedException>();
    }

    [Fact]
    public void StopLimitMissingStopPrice_Throws()
    {
        var req = BaseReq(OrderType.StopLimit, limit: 99m); // no stop price
        var act = () => Mapper().MapToJson(req);
        act.Should().Throw<ArgumentException>();
    }
}
