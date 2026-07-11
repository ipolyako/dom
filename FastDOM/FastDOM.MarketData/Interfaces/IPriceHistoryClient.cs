using FastDOM.MarketData.Models;

namespace FastDOM.MarketData.Interfaces;

public interface IPriceHistoryClient
{
    Task<IReadOnlyList<PriceCandle>> GetPriceHistoryAsync(
        string symbol, PriceHistoryRequest request, CancellationToken ct = default);
}
