using FastDOM.MarketData.Models;

namespace FastDOM.MarketData.Interfaces;

public interface IMarketMoversClient
{
    Task<IReadOnlyList<MarketMover>> GetMoversAsync(
        string indexSymbol,
        MoverSort sort,
        int frequency = 0,
        CancellationToken ct = default);
}
