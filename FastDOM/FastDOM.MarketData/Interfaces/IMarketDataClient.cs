using FastDOM.MarketData.Models;

namespace FastDOM.MarketData.Interfaces;

public interface IMarketDataClient : IAsyncDisposable
{
    bool IsConnected { get; }
    bool SupportsLevelTwo { get; }

    Task ConnectAsync(CancellationToken ct = default);
    Task DisconnectAsync(CancellationToken ct = default);

    Task<Quote?> GetSnapshotAsync(string symbol, CancellationToken ct = default);
    Task SubscribeQuotesAsync(string symbol, CancellationToken ct = default);
    Task UnsubscribeQuotesAsync(string symbol, CancellationToken ct = default);
    Task SubscribeDepthAsync(string symbol, CancellationToken ct = default);
    Task UnsubscribeDepthAsync(string symbol, CancellationToken ct = default);

    IObservable<Quote> QuoteStream { get; }
    IObservable<MarketDepth> DepthStream { get; }
    IObservable<Trade> TradeStream { get; }
    IObservable<bool> ConnectionStateStream { get; }
}
