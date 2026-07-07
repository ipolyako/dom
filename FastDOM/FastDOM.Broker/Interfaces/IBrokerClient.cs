using FastDOM.Core.Models;

namespace FastDOM.Broker.Interfaces;

public interface IBrokerClient : IAsyncDisposable
{
    bool IsConnected { get; }
    string BrokerName { get; }

    Task ConnectAsync(CancellationToken ct = default);
    Task DisconnectAsync(CancellationToken ct = default);
    Task<IReadOnlyList<AccountInfo>> GetAccountsAsync(CancellationToken ct = default);
    Task<AccountSummary> GetAccountSummaryAsync(string accountId, CancellationToken ct = default);
    Task<IReadOnlyList<OrderState>> GetOpenOrdersAsync(string accountId, CancellationToken ct = default);
    Task<OrderResult> PlaceOrderAsync(OrderRequest request, CancellationToken ct = default);
    Task<OrderResult> CancelOrderAsync(string accountId, string brokerOrderId, CancellationToken ct = default);
    Task<OrderResult> ReplaceOrderAsync(string accountId, OrderReplace replacement, CancellationToken ct = default);
    Task<OrderState?> GetOrderStatusAsync(string accountId, string brokerOrderId, CancellationToken ct = default);
    Task<IReadOnlyList<OrderState>> SyncOrdersAsync(string accountId, CancellationToken ct = default);

    IObservable<OrderState> OrderUpdateStream { get; }
    IObservable<bool> ConnectionStateStream { get; }
}

public class AccountInfo
{
    public required string AccountId { get; init; }
    public required string AccountHash { get; init; }
    public required string DisplayName { get; init; }
    public string? AccountType { get; init; }
    public override string ToString() => DisplayName;
}

public class OrderResult
{
    public bool Success { get; init; }
    public string? BrokerOrderId { get; init; }
    public string? ClientOrderId { get; init; }
    public string? ErrorMessage { get; init; }
    public string? RawResponse { get; init; }
    public int? HttpStatusCode { get; init; }

    public static OrderResult Ok(string? brokerId, string? clientId = null) =>
        new() { Success = true, BrokerOrderId = brokerId, ClientOrderId = clientId };

    public static OrderResult Fail(string error, int? code = null) =>
        new() { Success = false, ErrorMessage = error, HttpStatusCode = code };
}
