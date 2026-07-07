using System.Reactive.Linq;
using System.Reactive.Subjects;
using FastDOM.Broker.Interfaces;
using FastDOM.Core.Enums;
using FastDOM.Core.Models;
using Microsoft.Extensions.Logging;

namespace FastDOM.Broker.Mock;

public class MockBrokerClient : IBrokerClient
{
    private readonly ILogger<MockBrokerClient> _logger;
    private readonly Subject<OrderState> _orderSubject = new();
    private readonly Subject<bool> _connectionSubject = new();
    private readonly Dictionary<string, OrderState> _orders = [];
    private readonly Dictionary<string, AccountSummary> _accounts = [];
    private int _orderCounter;
    private bool _connected;

    public bool IsConnected => _connected;
    public string BrokerName => "MockBroker";
    public IObservable<OrderState> OrderUpdateStream => _orderSubject.AsObservable();
    public IObservable<bool> ConnectionStateStream => _connectionSubject.AsObservable();

    public MockBrokerClient(ILogger<MockBrokerClient> logger)
    {
        _logger = logger;
        InitMockAccounts();
    }

    private void InitMockAccounts()
    {
        _accounts["SIM001"] = new AccountSummary
        {
            AccountId = "SIM001",
            AccountName = "Simulation Account",
            BuyingPower = 100_000m,
            NetLiquidation = 100_000m,
            DayTradingBuyingPower = 400_000m,
            DailyRealizedPnL = 0m,
            DailyUnrealizedPnL = 0m
        };
    }

    public Task ConnectAsync(CancellationToken ct = default)
    {
        _connected = true;
        _connectionSubject.OnNext(true);
        _logger.LogInformation("MockBrokerClient connected");
        return Task.CompletedTask;
    }

    public Task DisconnectAsync(CancellationToken ct = default)
    {
        _connected = false;
        _connectionSubject.OnNext(false);
        return Task.CompletedTask;
    }

    public Task<IReadOnlyList<AccountInfo>> GetAccountsAsync(CancellationToken ct = default)
    {
        var list = new List<AccountInfo>
        {
            new() { AccountId = "SIM001", AccountHash = "SIM001HASH", DisplayName = "Simulation Account", AccountType = "MARGIN" }
        };
        return Task.FromResult<IReadOnlyList<AccountInfo>>(list);
    }

    public Task<AccountSummary> GetAccountSummaryAsync(string accountId, CancellationToken ct = default)
    {
        if (!_accounts.TryGetValue(accountId, out var acc))
            acc = _accounts["SIM001"];
        return Task.FromResult(acc);
    }

    public Task<IReadOnlyList<OrderState>> GetOpenOrdersAsync(string accountId, CancellationToken ct = default)
    {
        var open = _orders.Values
            .Where(o => o.AccountId == accountId && o.IsWorking)
            .ToList();
        return Task.FromResult<IReadOnlyList<OrderState>>(open);
    }

    public async Task<OrderResult> PlaceOrderAsync(OrderRequest request, CancellationToken ct = default)
    {
        await Task.Delay(50, ct); // simulate network
        var id = $"MOCK-{Interlocked.Increment(ref _orderCounter):D6}";
        var state = new OrderState
        {
            ClientOrderId = request.ClientOrderId,
            BrokerOrderId = id,
            AccountId = request.AccountId,
            Symbol = request.Symbol,
            Side = request.Side,
            QuantityOrdered = request.Quantity,
            OrderType = request.OrderType,
            LimitPrice = request.LimitPrice,
            StopPrice = request.StopPrice,
            Status = OrderStatus.Working,
            Source = request.Source,
            CreatedAtUtc = DateTime.UtcNow
        };
        _orders[id] = state;
        _orderSubject.OnNext(state);

        // Simulate fill for market orders after short delay
        if (request.OrderType == OrderType.Market)
        {
            _ = Task.Delay(200, CancellationToken.None).ContinueWith(_ => SimulateFill(id));
        }

        _logger.LogInformation("MockBroker placed order {Id} {Side} {Qty} {Symbol} @{Type}",
            id, request.Side, request.Quantity, request.Symbol, request.OrderType);
        return OrderResult.Ok(id, request.ClientOrderId);
    }

    public async Task<OrderResult> CancelOrderAsync(string accountId, string brokerOrderId, CancellationToken ct = default)
    {
        await Task.Delay(40, ct);
        if (!_orders.TryGetValue(brokerOrderId, out var state))
            return OrderResult.Fail("Order not found");

        if (state.IsTerminal)
            return OrderResult.Fail("Order already in terminal state");

        state.Transition(OrderStatus.Cancelled, "User cancelled");
        _orderSubject.OnNext(state);
        return OrderResult.Ok(brokerOrderId);
    }

    public async Task<OrderResult> ReplaceOrderAsync(string accountId, OrderReplace replacement, CancellationToken ct = default)
    {
        await Task.Delay(60, ct);
        if (!_orders.TryGetValue(replacement.BrokerOrderId, out var state))
            return OrderResult.Fail("Order not found");

        state.Transition(OrderStatus.CancelPending, "Replace requested");
        _orderSubject.OnNext(state);
        await Task.Delay(80, ct);

        var newId = $"MOCK-{Interlocked.Increment(ref _orderCounter):D6}";
        state.BrokerOrderId = newId;
        if (replacement.NewLimitPrice.HasValue) state.LimitPrice = replacement.NewLimitPrice;
        if (replacement.NewStopPrice.HasValue) state.StopPrice = replacement.NewStopPrice;
        state.Transition(OrderStatus.Working, "Replaced");
        _orders.Remove(replacement.BrokerOrderId);
        _orders[newId] = state;
        _orderSubject.OnNext(state);
        return OrderResult.Ok(newId);
    }

    public Task<OrderState?> GetOrderStatusAsync(string accountId, string brokerOrderId, CancellationToken ct = default)
    {
        _orders.TryGetValue(brokerOrderId, out var s);
        return Task.FromResult(s);
    }

    public Task<IReadOnlyList<OrderState>> SyncOrdersAsync(string accountId, CancellationToken ct = default)
    {
        var open = _orders.Values.Where(o => o.AccountId == accountId).ToList();
        return Task.FromResult<IReadOnlyList<OrderState>>(open);
    }

    private void SimulateFill(string id)
    {
        if (!_orders.TryGetValue(id, out var state)) return;
        if (state.IsTerminal) return;

        var fillPrice = state.LimitPrice ?? GetApproxPrice(state.Symbol);
        state.QuantityFilled = state.QuantityOrdered;
        state.AverageFillPrice = fillPrice;
        state.Transition(OrderStatus.Filled, "Simulated fill");

        UpdatePositionOnFill(state, fillPrice);
        _orderSubject.OnNext(state);
    }

    private static decimal GetApproxPrice(string symbol) => symbol switch
    {
        "SPY"  => 550m, "QQQ"  => 480m, "NVDA" => 135m,
        "TSLA" => 260m, "TQQQ" => 70m,  "SQQQ" => 8m,
        _      => 100m
    };

    private void UpdatePositionOnFill(OrderState state, decimal fillPrice)
    {
        if (!_accounts.TryGetValue(state.AccountId, out var acc)) return;

        var symbol = state.Symbol;
        if (!acc.Positions.TryGetValue(symbol, out var pos))
        {
            pos = new Position { AccountId = state.AccountId, Symbol = symbol };
            acc.Positions[symbol] = pos;
        }

        var prevQty  = pos.Quantity;
        var delta    = state.Side == OrderSide.Buy ? state.QuantityFilled : -state.QuantityFilled;
        var newQty   = prevQty + delta;

        if (prevQty == 0)
        {
            pos.AverageCost = fillPrice;
        }
        else if (newQty == 0 || Math.Sign(newQty) == Math.Sign(prevQty))
        {
            if (Math.Abs(delta) > 0 && Math.Abs(newQty) > Math.Abs(prevQty))
            {
                // Adding to position — blend avg cost
                pos.AverageCost = (pos.AverageCost * Math.Abs(prevQty) + fillPrice * Math.Abs(delta))
                                  / Math.Abs(newQty);
            }
            else
            {
                // Reducing / closing — realise P&L
                var sign = prevQty > 0 ? 1m : -1m;
                acc.DailyRealizedPnL += (fillPrice - pos.AverageCost) * Math.Abs(delta) * sign;
                if (newQty == 0) pos.AverageCost = 0;
            }
        }
        else
        {
            // Crossing flat — close old side, open new side
            var sign = prevQty > 0 ? 1m : -1m;
            acc.DailyRealizedPnL += (fillPrice - pos.AverageCost) * Math.Abs(prevQty) * sign;
            pos.AverageCost = fillPrice;
        }

        pos.Quantity = newQty;
        acc.BuyingPower -= delta * fillPrice;
    }

    public ValueTask DisposeAsync()
    {
        _orderSubject.Dispose();
        _connectionSubject.Dispose();
        return ValueTask.CompletedTask;
    }
}
