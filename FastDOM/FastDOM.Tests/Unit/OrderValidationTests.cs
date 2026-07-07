using FastDOM.Broker;
using FastDOM.Broker.Interfaces;
using FastDOM.Core.Enums;
using FastDOM.Core.Models;
using FastDOM.MarketData.Models;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace FastDOM.Tests.Unit;

public class OrderValidationTests
{
    private static RiskManager BuildRisk(RiskProfile? profile = null)
    {
        profile ??= new RiskProfile
        {
            LiveTradingEnabled = false,
            MaxSharesPerOrder = 100,
            MaxNotionalPerOrder = 25000m,
            MarketDataStaleMs = 2500,
            AllowShortSelling = false,
            MaxOrdersPerMinute = 20
        };
        return new RiskManager(NullLogger<RiskManager>.Instance, profile);
    }

    private static AccountSummary EmptyAccount(string id = "ACC1") =>
        new() { AccountId = id, AccountName = id, BuyingPower = 100_000m };

    private static Quote FreshQuote(string symbol, decimal last = 100m) =>
        new() { Symbol = symbol, Last = last, Bid = last - 0.01m, Ask = last + 0.01m, TimestampUtc = DateTime.UtcNow };

    private static OrderRequest BuyLimit(string accountId = "ACC1", string symbol = "SPY",
        int qty = 10, decimal limit = 100m) =>
        new()
        {
            AccountId = accountId, Symbol = symbol, Side = OrderSide.Buy, Quantity = qty,
            OrderType = OrderType.Limit, LimitPrice = limit, Source = OrderSource.DomClick
        };

    [Fact]
    public void ValidOrder_Passes()
    {
        var risk = BuildRisk();
        var result = risk.ValidateOrder(BuyLimit(), EmptyAccount(), FreshQuote("SPY"));
        result.IsValid.Should().BeTrue();
    }

    [Fact]
    public void ZeroQuantity_Rejected()
    {
        var risk = BuildRisk();
        var req = BuyLimit(qty: 0);
        var result = risk.ValidateOrder(req, EmptyAccount(), FreshQuote("SPY"));
        result.IsValid.Should().BeFalse();
        result.RejectCode.Should().Be(RiskRejectCode.InvalidQuantity);
    }

    [Fact]
    public void QuantityExceedsMax_Rejected()
    {
        var risk = BuildRisk();
        var req = BuyLimit(qty: 200); // max is 100
        var result = risk.ValidateOrder(req, EmptyAccount(), FreshQuote("SPY"));
        result.IsValid.Should().BeFalse();
        result.RejectCode.Should().Be(RiskRejectCode.ExceedsMaxShares);
    }

    [Fact]
    public void NotionalExceedsMax_Rejected()
    {
        var risk = BuildRisk();
        var req = BuyLimit(qty: 100, limit: 300m); // 100 * 300 = 30000 > 25000
        var result = risk.ValidateOrder(req, EmptyAccount(), FreshQuote("SPY", 300m));
        result.IsValid.Should().BeFalse();
        result.RejectCode.Should().Be(RiskRejectCode.ExceedsMaxNotional);
    }

    [Fact]
    public void StaleMarketData_Rejected()
    {
        var risk = BuildRisk(new RiskProfile
        {
            MarketDataStaleMs = 100,
            DisableOpeningOrdersWhenMarketDataStale = true,
            MaxSharesPerOrder = 1000,
            MaxNotionalPerOrder = 1_000_000m
        });
        var staleQuote = new Quote
        {
            Symbol = "SPY", Last = 100m, Bid = 99.99m, Ask = 100.01m,
            TimestampUtc = DateTime.UtcNow.AddMilliseconds(-500)
        };
        var result = risk.ValidateOrder(BuyLimit(), EmptyAccount(), staleQuote);
        result.IsValid.Should().BeFalse();
        result.RejectCode.Should().Be(RiskRejectCode.MarketDataStale);
    }

    [Fact]
    public void BlacklistedSymbol_Rejected()
    {
        var risk = BuildRisk(new RiskProfile { SymbolBlacklist = ["GME"], MaxSharesPerOrder = 1000 });
        var req = new OrderRequest
        {
            AccountId = "ACC1", Symbol = "GME", Side = OrderSide.Buy, Quantity = 10,
            OrderType = OrderType.Limit, LimitPrice = 20m, Source = OrderSource.DomClick
        };
        var result = risk.ValidateOrder(req, EmptyAccount(), FreshQuote("GME", 20m));
        result.IsValid.Should().BeFalse();
        result.RejectCode.Should().Be(RiskRejectCode.SymbolNotAllowed);
    }

    [Fact]
    public void SymbolNotWhitelisted_Rejected()
    {
        var risk = BuildRisk(new RiskProfile { SymbolWhitelist = ["SPY", "QQQ"], MaxSharesPerOrder = 1000 });
        var req = BuyLimit(symbol: "MEME");
        var result = risk.ValidateOrder(req, EmptyAccount(), FreshQuote("MEME"));
        result.IsValid.Should().BeFalse();
        result.RejectCode.Should().Be(RiskRejectCode.SymbolNotAllowed);
    }

    [Fact]
    public void KillSwitch_BlocksAllOrders()
    {
        var risk = BuildRisk();
        risk.ActivateKillSwitch();
        var result = risk.ValidateOrder(BuyLimit(), EmptyAccount(), FreshQuote("SPY"));
        result.IsValid.Should().BeFalse();
        result.RejectCode.Should().Be(RiskRejectCode.KillSwitchActive);
    }

    [Fact]
    public void HighNotional_RequiresConfirmation()
    {
        var risk = BuildRisk(new RiskProfile
        {
            MaxSharesPerOrder = 1000,
            MaxNotionalPerOrder = 1_000_000m,
            RequireConfirmationAboveNotional = 5000m
        });
        var req = BuyLimit(qty: 100, limit: 100m); // 10000 > 5000
        var result = risk.ValidateOrder(req, EmptyAccount(), FreshQuote("SPY", 100m));
        result.IsValid.Should().BeTrue();
        result.RequiresConfirmation.Should().BeTrue();
    }

    [Fact]
    public void ShortSale_WhenNotAllowed_Rejected()
    {
        var risk = BuildRisk(new RiskProfile { AllowShortSelling = false, MaxSharesPerOrder = 1000 });
        var account = EmptyAccount();
        account.Positions["SPY"] = new Position
        {
            AccountId = "ACC1", Symbol = "SPY", Quantity = 0, AverageCost = 0
        };
        var req = new OrderRequest
        {
            AccountId = "ACC1", Symbol = "SPY", Side = OrderSide.Sell, Quantity = 10,
            OrderType = OrderType.Market, Source = OrderSource.HotButton
        };
        var result = risk.ValidateOrder(req, account, FreshQuote("SPY"));
        result.IsValid.Should().BeFalse();
        result.RejectCode.Should().Be(RiskRejectCode.ShortSaleNotAllowed);
    }
}
