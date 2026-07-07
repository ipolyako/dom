using FastDOM.MarketData.Models;

namespace FastDOM.MarketData.Interfaces;

public interface IOptionsDataProvider
{
    Task<IReadOnlyList<DateOnly>> GetExpirationDatesAsync(string underlying, CancellationToken ct = default);
    Task<IReadOnlyList<OptionsChainRow>> GetChainAsync(string underlying, DateOnly expiration, CancellationToken ct = default);
}
