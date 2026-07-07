using System.Reactive.Subjects;
using FastDOM.Broker.Interfaces;
using FastDOM.Core.Models;
using Microsoft.Extensions.Logging;

namespace FastDOM.Broker.Proxies;

/// <summary>
/// Singleton DI entry-point for the active broker. Swap the inner client at runtime without
/// rebuilding the DI container.
/// </summary>
public class RuntimeBrokerProxy : IBrokerClient
{
    private readonly ILogger<RuntimeBrokerProxy> _logger;
    private readonly Subject<OrderState> _orderSubject = new();
    private readonly Subject<bool> _connectionSubject = new();
    private IDisposable? _orderSub;
    private IDisposable? _connSub;
    private IBrokerClient? _inner;

    public bool IsConnected => _inner?.IsConnected ?? false;
    public string BrokerName => _inner?.BrokerName ?? "None";
    public IObservable<OrderState> OrderUpdateStream => _orderSubject;
    public IObservable<bool> ConnectionStateStream => _connectionSubject;

    public RuntimeBrokerProxy(ILogger<RuntimeBrokerProxy> logger) => _logger = logger;

    public async Task SwapAsync(IBrokerClient newClient, CancellationToken ct = default)
    {
        if (_inner != null)
        {
            _orderSub?.Dispose();
            _connSub?.Dispose();
            try { await _inner.DisconnectAsync(ct); } catch { /* best-effort */ }
            await _inner.DisposeAsync();
        }

        _inner = newClient;
        _orderSub = newClient.OrderUpdateStream.Subscribe(_orderSubject);
        _connSub  = newClient.ConnectionStateStream.Subscribe(_connectionSubject);
        _logger.LogInformation("Broker swapped to {Name}", newClient.BrokerName);
    }

    public Task ConnectAsync(CancellationToken ct = default)
        => _inner?.ConnectAsync(ct) ?? Task.CompletedTask;

    public Task DisconnectAsync(CancellationToken ct = default)
        => _inner?.DisconnectAsync(ct) ?? Task.CompletedTask;

    public Task<IReadOnlyList<AccountInfo>> GetAccountsAsync(CancellationToken ct = default)
        => _inner?.GetAccountsAsync(ct) ?? Task.FromResult<IReadOnlyList<AccountInfo>>([]);

    public Task<AccountSummary> GetAccountSummaryAsync(string accountId, CancellationToken ct = default)
        => _inner?.GetAccountSummaryAsync(accountId, ct)
           ?? Task.FromResult(new AccountSummary { AccountId = accountId, AccountName = "" });

    public Task<IReadOnlyList<OrderState>> GetOpenOrdersAsync(string accountId, CancellationToken ct = default)
        => _inner?.GetOpenOrdersAsync(accountId, ct) ?? Task.FromResult<IReadOnlyList<OrderState>>([]);

    public Task<OrderResult> PlaceOrderAsync(OrderRequest request, CancellationToken ct = default)
        => _inner?.PlaceOrderAsync(request, ct) ?? Task.FromResult(OrderResult.Fail("No broker connected"));

    public Task<OrderResult> CancelOrderAsync(string accountId, string brokerOrderId, CancellationToken ct = default)
        => _inner?.CancelOrderAsync(accountId, brokerOrderId, ct) ?? Task.FromResult(OrderResult.Fail("No broker connected"));

    public Task<OrderResult> ReplaceOrderAsync(string accountId, OrderReplace replacement, CancellationToken ct = default)
        => _inner?.ReplaceOrderAsync(accountId, replacement, ct) ?? Task.FromResult(OrderResult.Fail("No broker connected"));

    public Task<OrderState?> GetOrderStatusAsync(string accountId, string brokerOrderId, CancellationToken ct = default)
        => _inner?.GetOrderStatusAsync(accountId, brokerOrderId, ct) ?? Task.FromResult<OrderState?>(null);

    public Task<IReadOnlyList<OrderState>> SyncOrdersAsync(string accountId, CancellationToken ct = default)
        => _inner?.SyncOrdersAsync(accountId, ct) ?? Task.FromResult<IReadOnlyList<OrderState>>([]);

    public async ValueTask DisposeAsync()
    {
        _orderSub?.Dispose();
        _connSub?.Dispose();
        _orderSubject.Dispose();
        _connectionSubject.Dispose();
        if (_inner != null)
            await _inner.DisposeAsync();
    }
}
