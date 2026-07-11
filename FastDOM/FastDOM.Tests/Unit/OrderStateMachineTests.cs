using FastDOM.Core.Enums;
using FastDOM.Core.Models;
using FluentAssertions;
using Xunit;

namespace FastDOM.Tests.Unit;

public class OrderStateMachineTests
{
    private static OrderState MakeState(OrderStatus initial = OrderStatus.Working) =>
        new()
        {
            ClientOrderId = "C001",
            AccountId = "ACC1",
            Symbol = "SPY",
            Side = OrderSide.Buy,
            QuantityOrdered = 10,
            OrderType = OrderType.Limit,
            LimitPrice = 100m,
            Status = initial,
            Source = OrderSource.DomClick
        };

    [Fact]
    public void Transition_ChangesStatus()
    {
        var state = MakeState(OrderStatus.Working);
        state.Transition(OrderStatus.CancelPending);
        state.Status.Should().Be(OrderStatus.CancelPending);
    }

    [Fact]
    public void Transition_RecordsHistory()
    {
        var state = MakeState(OrderStatus.Working);
        state.Transition(OrderStatus.CancelPending, "user cancel");
        state.StatusHistory.Should().HaveCount(1);
        state.StatusHistory[0].Should().Contain("CancelPending");
        state.StatusHistory[0].Should().Contain("user cancel");
    }

    [Fact]
    public void IsWorking_TrueForEveryNonTerminalBrokerOrderState()
    {
        foreach (var s in new[]
                 {
                     OrderStatus.Submitted,
                     OrderStatus.Accepted,
                     OrderStatus.Working,
                     OrderStatus.PartiallyFilled,
                     OrderStatus.CancelPending,
                     OrderStatus.ReplacePending
                 })
        {
            MakeState(s).IsWorking.Should().BeTrue();
        }
    }

    [Fact]
    public void IsTerminal_TrueForTerminalStates()
    {
        foreach (var s in new[] { OrderStatus.Filled, OrderStatus.Cancelled, OrderStatus.BrokerRejected,
                                   OrderStatus.RejectedLocally, OrderStatus.Error })
        {
            MakeState(s).IsTerminal.Should().BeTrue();
        }
    }

    [Fact]
    public void QuantityRemaining_CorrectAfterPartialFill()
    {
        var state = MakeState();
        state.QuantityFilled = 3;
        state.QuantityRemaining.Should().Be(7);
    }
}
