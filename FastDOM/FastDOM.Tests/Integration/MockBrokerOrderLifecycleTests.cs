using System.IO;
using FastDOM.App.Services;
using FastDOM.Broker;
using FastDOM.Broker.Mock;
using FastDOM.Core.Enums;
using FastDOM.Core.Models;
using FastDOM.Infrastructure.Config;
using FastDOM.Infrastructure.Logging;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace FastDOM.Tests.Integration;

public class MockBrokerOrderLifecycleTests : IAsyncLifetime
{
    private MockBrokerClient _broker = null!;
    private RiskManager _risk = null!;
    private OrderService _orderService = null!;
    private AuditLogger _audit = null!;

    public async Task InitializeAsync()
    {
        _broker = new MockBrokerClient(NullLogger<MockBrokerClient>.Instance);
        _risk = new RiskManager(NullLogger<RiskManager>.Instance, new RiskProfile
        {
            MaxSharesPerOrder = 1000, MaxNotionalPerOrder = 1_000_000m,
            DisableOpeningOrdersWhenMarketDataStale = false
        });

        var logDir = Path.Combine(Path.GetTempPath(), "fastdom-test-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(logDir);
        _audit = new AuditLogger(NullLogger<AuditLogger>.Instance, logDir);

        _orderService = new OrderService(
            NullLogger<OrderService>.Instance, _broker, _risk, _audit);

        await _broker.ConnectAsync();
    }

    public Task DisposeAsync() => _broker.DisposeAsync().AsTask();

    private AccountSummary MockAccount() =>
        new() { AccountId = "SIM001", AccountName = "Sim", BuyingPower = 100_000m };

    [Fact]
    public async Task PlaceLimitOrder_WorksThenCancels()
    {
        var req = new OrderRequest
        {
            AccountId = "SIM001", Symbol = "SPY", Side = OrderSide.Buy, Quantity = 10,
            OrderType = OrderType.Limit, LimitPrice = 100m, Source = OrderSource.DomClick
        };

        var (success, _) = await _orderService.SubmitOrderAsync(req, MockAccount(), null);
        success.Should().BeTrue();

        var working = _orderService.ActiveOrders.Values
            .Where(o => o.Symbol == "SPY" && o.IsWorking).ToList();
        working.Should().HaveCountGreaterOrEqualTo(1);

        var order = working.First();
        var (cancelOk, _) = await _orderService.CancelOrderAsync("SIM001", order.BrokerOrderId!);
        cancelOk.Should().BeTrue();

        _orderService.ActiveOrders[order.BrokerOrderId!].Status.Should().Be(OrderStatus.Cancelled);
    }

    [Fact]
    public async Task PlaceMarketOrder_GetsFilledAutomatically()
    {
        var req = new OrderRequest
        {
            AccountId = "SIM001", Symbol = "NVDA", Side = OrderSide.Buy, Quantity = 5,
            OrderType = OrderType.Market, Source = OrderSource.HotButton
        };

        var (success, _) = await _orderService.SubmitOrderAsync(req, MockAccount(), null);
        success.Should().BeTrue();

        // Mock fills market orders after 200ms
        await Task.Delay(500);

        var filled = _orderService.ActiveOrders.Values
            .Where(o => o.Symbol == "NVDA" && o.Status == OrderStatus.Filled).ToList();
        filled.Should().HaveCountGreaterOrEqualTo(1);
    }

    [Fact]
    public async Task ReplaceOrder_ChangesPrice()
    {
        var req = new OrderRequest
        {
            AccountId = "SIM001", Symbol = "QQQ", Side = OrderSide.Buy, Quantity = 10,
            OrderType = OrderType.Limit, LimitPrice = 100m, Source = OrderSource.DomClick
        };

        await _orderService.SubmitOrderAsync(req, MockAccount(), null);
        var order = _orderService.ActiveOrders.Values
            .First(o => o.Symbol == "QQQ" && o.IsWorking);

        var replacement = new OrderReplace
        {
            OriginalClientOrderId = order.ClientOrderId,
            BrokerOrderId = order.BrokerOrderId!,
            NewLimitPrice = 101m,
            Source = OrderSource.DomClick
        };

        var (ok, _) = await _orderService.ReplaceOrderAsync("SIM001", replacement);
        ok.Should().BeTrue();

        var replaced = _orderService.ActiveOrders.Values
            .FirstOrDefault(o => o.LimitPrice == 101m);
        replaced.Should().NotBeNull();
    }

    [Fact]
    public async Task CancelAll_CancelsWorkingOrders()
    {
        for (int i = 0; i < 3; i++)
        {
            var req = new OrderRequest
            {
                AccountId = "SIM001", Symbol = "TSLA", Side = OrderSide.Buy, Quantity = 5,
                OrderType = OrderType.Limit, LimitPrice = 200m + i,
                Source = OrderSource.DomClick
            };
            await _orderService.SubmitOrderAsync(req, MockAccount(), null);
        }

        await _orderService.CancelAllForSymbolAsync("SIM001", "TSLA");
        await Task.Delay(200);

        var stillWorking = _orderService.ActiveOrders.Values
            .Where(o => o.Symbol == "TSLA" && o.IsWorking).ToList();
        stillWorking.Should().BeEmpty();
    }

    [Fact]
    public async Task RiskReject_BlocksInvalidOrder()
    {
        var risk = new RiskManager(NullLogger<RiskManager>.Instance, new RiskProfile
        {
            MaxSharesPerOrder = 5
        });
        var service = new OrderService(NullLogger<OrderService>.Instance, _broker, risk, _audit);

        var req = new OrderRequest
        {
            AccountId = "SIM001", Symbol = "SPY", Side = OrderSide.Buy, Quantity = 100,
            OrderType = OrderType.Limit, LimitPrice = 100m, Source = OrderSource.DomClick
        };

        var (success, msg) = await service.SubmitOrderAsync(req, MockAccount(), null);
        success.Should().BeFalse();
        msg.Should().Contain("max");
    }
}
