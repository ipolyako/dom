using System.Text.Json;
using FastDOM.Broker.Schwab.Client;
using FastDOM.MarketData.Models;
using FluentAssertions;
using Xunit;

namespace FastDOM.Tests.Unit;

public class SchwabStreamerProtocolTests
{
    [Fact]
    public void EquityFields_AreDecodedUsingEquityContract()
    {
        using var doc = JsonDocument.Parse("""{"1":100.1,"2":100.2,"3":100.15,"4":4,"5":5,"8":12345,"9":7,"10":101,"11":99,"12":98,"17":99.5,"18":2.15,"42":2.19}""");
        var quote = new Quote { Symbol = "TEST" };

        SchwabMarketDataClient.ApplyEquityFields(doc.RootElement, quote).Should().BeTrue();

        quote.Bid.Should().Be(100.1m);
        quote.Ask.Should().Be(100.2m);
        quote.Last.Should().Be(100.15m);
        quote.BidSize.Should().Be(4);
        quote.AskSize.Should().Be(5);
        quote.LastSize.Should().Be(7);
        quote.Volume.Should().Be(12345);
        quote.Open.Should().Be(99.5m);
        quote.NetChangePct.Should().Be(2.19m);
    }

    [Fact]
    public void OptionFields_AreDecodedUsingOptionContract()
    {
        using var doc = JsonDocument.Parse("""{"1":"description","2":6.5,"3":6.7,"4":6.6,"8":900,"15":6.1,"16":12,"17":14,"18":3}""");
        var quote = new Quote { Symbol = "SPY260710C00752000" };

        SchwabMarketDataClient.ApplyOptionFields(doc.RootElement, quote).Should().BeTrue();

        quote.Bid.Should().Be(6.5m);
        quote.Ask.Should().Be(6.7m);
        quote.Last.Should().Be(6.6m);
        quote.BidSize.Should().Be(12);
        quote.AskSize.Should().Be(14);
        quote.LastSize.Should().Be(3);
        quote.Volume.Should().Be(900);
    }

    [Fact]
    public void FutureFields_AreDecodedUsingFutureContract()
    {
        using var doc = JsonDocument.Parse("""{"1":25750.25,"2":25750.50,"3":25750.25,"4":18,"5":21,"8":123456,"9":2,"12":25800,"13":25500,"14":25600,"18":25625,"19":150.25,"20":0.59}""");
        var quote = new Quote { Symbol = "/NQ" };

        SchwabMarketDataClient.ApplyFutureFields(doc.RootElement, quote).Should().BeTrue();

        quote.Bid.Should().Be(25750.25m);
        quote.Ask.Should().Be(25750.50m);
        quote.Last.Should().Be(25750.25m);
        quote.BidSize.Should().Be(18);
        quote.AskSize.Should().Be(21);
        quote.LastSize.Should().Be(2);
        quote.Volume.Should().Be(123456);
        quote.Open.Should().Be(25625m);
        quote.NetChangePct.Should().Be(0.59m);
    }

    [Fact]
    public void WholeBookUpdate_ReplacesAbsentLevels()
    {
        var levels = new List<DomLevel>
        {
            new() { Price = 99m, BidSize = 100 },
            new() { Price = 98m, BidSize = 200 }
        };
        using var doc = JsonDocument.Parse("""[{"0":100.0,"1":300}]""");

        SchwabMarketDataClient.ReplaceBookLevels(doc.RootElement, levels, isBid: true);

        levels.Should().ContainSingle();
        levels[0].Price.Should().Be(100m);
        levels[0].BidSize.Should().Be(300);
    }

    [Fact]
    public void EquityAndOptionSubscriptions_RequestDifferentFields()
    {
        SchwabMarketDataClient.EquityFields.Should().Contain("1,2,3,4,5");
        SchwabMarketDataClient.OptionFields.Should().Contain("2,3,4");
        SchwabMarketDataClient.OptionFields.Should().Contain("16,17,18");
        SchwabMarketDataClient.FutureFields.Should().Contain("1,2,3,4,5");
        SchwabMarketDataClient.EquityFields.Should().NotBe(SchwabMarketDataClient.OptionFields);
        SchwabMarketDataClient.FutureFields.Should().NotBe(SchwabMarketDataClient.EquityFields);
    }

    [Fact]
    public void SnapshotFallback_IsDisabledOnWeekends()
    {
        var saturday = new DateTime(2026, 7, 11, 15, 0, 0, DateTimeKind.Utc);
        SchwabMarketDataClient.IsExtendedEquitySession(saturday).Should().BeFalse();
    }
}
