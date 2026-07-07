using FastDOM.Core.Models;
using FluentAssertions;
using Xunit;

namespace FastDOM.Tests.Unit;

public class TickRoundingTests
{
    [Theory]
    [InlineData(100.005, 0.01, 100.01)]
    [InlineData(100.004, 0.01, 100.00)]
    [InlineData(100.125, 0.25, 100.25)]
    [InlineData(100.001, 0.01, 100.00)]
    [InlineData(549.997, 0.01, 550.00)]
    [InlineData(0.0, 0.01, 0.0)]
    public void RoundToTick_ReturnsExpected(decimal price, decimal tick, decimal expected)
    {
        var symbol = SymbolInfo.Default("TEST");
        symbol.TickSize = tick;
        symbol.RoundToTick(price).Should().Be(expected);
    }

    [Theory]
    [InlineData(100.00, 0.01, true)]
    [InlineData(100.005, 0.01, false)]
    [InlineData(100.25, 0.25, true)]
    [InlineData(100.10, 0.25, false)]
    public void IsTickAligned_ReturnsExpected(decimal price, decimal tick, bool expected)
    {
        var symbol = SymbolInfo.Default("TEST");
        symbol.TickSize = tick;
        symbol.IsTickAligned(price).Should().Be(expected);
    }
}
