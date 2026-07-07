using System.Diagnostics;
using FastDOM.Broker.Interfaces;
using FastDOM.Core.Enums;
using FastDOM.Core.Models;
using FastDOM.Infrastructure.Logging;
using FastDOM.MarketData.Models;
using Microsoft.Extensions.Logging;

namespace FastDOM.App.Services;

/// <summary>
/// Orchestrates the full order lifecycle: validate → submit → track → audit.
/// </summary>
public class OrderService
{
    private readonly ILogger<OrderService> _logger;
    private readonly IBrokerClient _broker;
    private readonly IRiskManager _risk;
    private readonly AuditLogger _audit;
    private readonly Dictionary<string, OrderState> _activeOrders = [];

    public event Action<OrderState>? OrderStateChanged;
    public event Action<string>? ToastRequested;

    public IReadOnlyDictionary<string, OrderState> ActiveOrders => _activeOrders;

    public OrderService(
        ILogger<OrderService> logger,
        IBrokerClient broker,
        IRiskManager risk,
        AuditLogger audit)
    {
        _logger = logger;
        _broker = broker;
        _risk = risk;
        _audit = audit;

        _broker.OrderUpdateStream.Subscribe(OnBrokerOrderUpdate);
    }

    public async Task<(bool success, string message)> SubmitOrderAsync(
        OrderRequest request,
        AccountSummary account,
        Quote? quote,
        bool bypassConfirmation = false)
    {
        var sw = Stopwatch.StartNew();

        // 1. Local validation
        var validation = _risk.ValidateOrder(request, account, quote);
        if (!validation.IsValid)
        {
            await _audit.LogRiskRejectAsync(validation.RejectReason!, request.AccountId, request.Symbol,
                request.Source.ToString());
            _logger.LogWarning("Order rejected locally: {Reason}", validation.RejectReason);
            ToastRequested?.Invoke($"REJECTED: {validation.RejectReason}");
            return (false, validation.RejectReason!);
        }

        if (validation.RequiresConfirmation && !bypassConfirmation)
            return (false, $"CONFIRM_REQUIRED:{validation.ConfirmationMessage}");

        // 2. Create draft state
        var state = new OrderState
        {
            ClientOrderId = request.ClientOrderId,
            AccountId     = request.AccountId,
            Symbol        = request.Symbol,
            Side          = request.Side,
            QuantityOrdered = request.Quantity,
            OrderType     = request.OrderType,
            LimitPrice    = request.LimitPrice,
            StopPrice     = request.StopPrice,
            Status        = OrderStatus.Submitting,
            Source        = request.Source
        };

        _activeOrders[request.ClientOrderId] = state;
        OrderStateChanged?.Invoke(state);
        _risk.RecordOrderSubmitted(request);

        var localValidationMs = sw.ElapsedMilliseconds;
        sw.Restart();

        // 3. Submit to broker
        await _audit.LogOrderSubmittedAsync(request, localValidationMs);

        OrderResult result;
        try
        {
            result = await _broker.PlaceOrderAsync(request);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Broker PlaceOrder threw exception");
            result = OrderResult.Fail(ex.Message);
        }

        var brokerAckMs = sw.ElapsedMilliseconds;
        await _audit.LogOrderResultAsync(result.Success, result.BrokerOrderId, result.ErrorMessage,
            result.HttpStatusCode, request.ClientOrderId, brokerAckMs);

        if (result.Success)
        {
            state.BrokerOrderId = result.BrokerOrderId;
            state.Transition(OrderStatus.Accepted);
            _activeOrders[result.BrokerOrderId!] = state;
            ToastRequested?.Invoke($"Order {request.Side} {request.Quantity} {request.Symbol} ACCEPTED ({brokerAckMs}ms)");
        }
        else
        {
            state.Transition(OrderStatus.BrokerRejected, result.ErrorMessage);
            ToastRequested?.Invoke($"BROKER REJECTED: {result.ErrorMessage}");
        }

        OrderStateChanged?.Invoke(state);
        _logger.LogInformation("Order {Id} → {Status} in {Ms}ms", state.ClientOrderId, state.Status, brokerAckMs);
        return (result.Success, result.ErrorMessage ?? "OK");
    }

    public async Task<(bool success, string message)> CancelOrderAsync(string accountId, string brokerOrderId)
    {
        if (!_activeOrders.TryGetValue(brokerOrderId, out var state))
            return (false, "Order not found");

        state.Transition(OrderStatus.CancelPending);
        OrderStateChanged?.Invoke(state);

        var result = await _broker.CancelOrderAsync(accountId, brokerOrderId);
        if (result.Success)
        {
            // The broker stream may have already transitioned to Cancelled via OnBrokerOrderUpdate
            if (!state.IsTerminal)
                state.Transition(OrderStatus.Cancelled);
            _logger.LogInformation("Order {Id} cancelled", brokerOrderId);
        }
        else
        {
            // Do not mark as cancelled if it failed — keep it as working
            state.Transition(OrderStatus.Working, "Cancel failed: " + result.ErrorMessage);
            _logger.LogWarning("Cancel failed for {Id}: {Error}", brokerOrderId, result.ErrorMessage);
        }

        OrderStateChanged?.Invoke(state);
        return (result.Success, result.ErrorMessage ?? "OK");
    }

    public async Task<(bool success, string message)> ReplaceOrderAsync(
        string accountId, OrderReplace replacement)
    {
        if (!_activeOrders.TryGetValue(replacement.BrokerOrderId, out var state))
            return (false, "Order not found");

        state.Transition(OrderStatus.ReplacePending);
        OrderStateChanged?.Invoke(state);

        var result = await _broker.ReplaceOrderAsync(accountId, replacement);
        if (result.Success)
        {
            if (replacement.NewLimitPrice.HasValue) state.LimitPrice = replacement.NewLimitPrice;
            if (replacement.NewStopPrice.HasValue) state.StopPrice = replacement.NewStopPrice;
            state.Transition(OrderStatus.Working);
        }
        else
        {
            state.Transition(OrderStatus.Working, "Replace failed: " + result.ErrorMessage);
        }

        OrderStateChanged?.Invoke(state);
        return (result.Success, result.ErrorMessage ?? "OK");
    }

    public async Task CancelAllForSymbolAsync(string accountId, string symbol)
    {
        // Deduplicate because each order is stored under both ClientOrderId and BrokerOrderId keys
        var working = _activeOrders.Values
            .Where(o => o.AccountId == accountId && o.Symbol == symbol && o.IsWorking)
            .DistinctBy(o => o.BrokerOrderId ?? o.ClientOrderId)
            .ToList();

        _logger.LogInformation("CancelAll for {Symbol}: {Count} orders", symbol, working.Count);
        foreach (var o in working)
            await CancelOrderAsync(accountId, o.BrokerOrderId!);
    }

    public async Task CancelSideForSymbolAsync(string accountId, string symbol, OrderSide side)
    {
        var working = _activeOrders.Values
            .Where(o => o.AccountId == accountId && o.Symbol == symbol
                        && o.Side == side && o.IsWorking)
            .DistinctBy(o => o.BrokerOrderId ?? o.ClientOrderId)
            .ToList();
        foreach (var o in working)
            await CancelOrderAsync(accountId, o.BrokerOrderId!);
    }

    public async Task SyncOrdersAsync(string accountId)
    {
        var orders = await _broker.SyncOrdersAsync(accountId);
        foreach (var o in orders)
        {
            if (o.BrokerOrderId != null)
                _activeOrders[o.BrokerOrderId] = o;
        }
        _logger.LogInformation("Synced {Count} orders for {Account}", orders.Count, accountId);
    }

    private void OnBrokerOrderUpdate(OrderState update)
    {
        if (update.BrokerOrderId != null && _activeOrders.TryGetValue(update.BrokerOrderId, out var existing))
        {
            existing.Transition(update.Status, update.BrokerMessage);
            existing.QuantityFilled = update.QuantityFilled;
            existing.AverageFillPrice = update.AverageFillPrice;
        }
        else if (update.ClientOrderId != null && _activeOrders.TryGetValue(update.ClientOrderId, out var byClient))
        {
            byClient.BrokerOrderId = update.BrokerOrderId;
            byClient.Transition(update.Status);
            if (update.BrokerOrderId != null)
                _activeOrders[update.BrokerOrderId] = byClient;
        }

        OrderStateChanged?.Invoke(update);
        _ = _audit.LogOrderStateChangeAsync(update);
    }
}
