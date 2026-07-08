using FastDOM.MarketData.Interfaces;
using FastDOM.MarketData.Models;

namespace FastDOM.MarketData.Mock;

public class MockOptionsDataProvider : IOptionsDataProvider
{
    public Task<IReadOnlyList<DateOnly>> GetExpirationDatesAsync(string underlying, CancellationToken ct = default)
        => Task.FromResult<IReadOnlyList<DateOnly>>([]);

    public Task<IReadOnlyList<OptionsChainRow>> GetChainAsync(
        string underlying,
        DateOnly expiration,
        CancellationToken ct = default)
        => Task.FromResult<IReadOnlyList<OptionsChainRow>>([]);
}
