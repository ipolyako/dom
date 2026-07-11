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
    private readonly Dictionary<string, decimal> _todayRealizedPnL = new(StringComparer.OrdinalIgnoreCase);

    public event Action<OrderState>? OrderStateChanged;
    public event Action<string>? ToastRequested;

    public IReadOnlyDictionary<string, OrderState> ActiveOrders => _activeOrders;

    public decimal? GetTodayRealizedPnL(string accountId, string symbol)
    {
        var key = PnLKey(accountId, symbol);
        return _todayRealizedPnL.TryGetValue(key, out var pnl) ? pnl : null;
    }

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
        var totalSw = Stopwatch.StartNew();
        _logger.LogInformation(
            "[LATENCY] Submit start client={ClientOrderId} account={Account} symbol={Symbol} side={Side} qty={Qty} type={Type}",
            request.ClientOrderId, request.AccountId, request.Symbol, request.Side, request.Quantity, request.OrderType);

        // 1. Local validation
        // Extended-hours orders must be Limit + Day (Alpaca + Schwab both enforce this).
        if (request.ExtendedHours)
        {
            string? extErr = null;
            if (request.OrderType != OrderType.Limit && request.OrderType != OrderType.MarketableLimit)
                extErr = $"Extended-hours orders must be Limit type (got {request.OrderType})";
            else if (request.TimeInForce != TimeInForce.Day)
                extErr = $"Extended-hours orders must use Day TIF (got {request.TimeInForce})";
            if (extErr != null)
            {
                _logger.LogWarning("Order rejected locally: {Reason}", extErr);
                ToastRequested?.Invoke($"REJECTED: {extErr}");
                return (false, extErr);
            }
        }

        if (IsStopOrder(request.OrderType) && request.StopPrice.HasValue && quote != null)
        {
            var mid = quote.Last > 0 ? quote.Last : (quote.Ask + quote.Bid) / 2m;
            if (mid > 0)
            {
                var msg = request.Side == OrderSide.Buy && request.StopPrice <= mid
                    ? $"Buy stop price {request.StopPrice:F2} must be above current price {mid:F2}"
                    : request.Side == OrderSide.Sell && request.StopPrice >= mid
                    ? $"Sell stop price {request.StopPrice:F2} must be below current price {mid:F2}"
                    : null;
                if (msg != null)
                {
                    _logger.LogWarning("Stop order rejected locally: {Reason}", msg);
                    ToastRequested?.Invoke($"REJECTED: {msg}");
                    return (false, msg);
                }
            }
        }

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
        _logger.LogInformation(
            "[LATENCY] Submit local-ready client={ClientOrderId} validationMs={ValidationMs}",
            request.ClientOrderId, localValidationMs);
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
        _logger.LogInformation(
            "[LATENCY] Submit broker-ack client={ClientOrderId} broker={BrokerOrderId} success={Success} http={HttpStatus} brokerMs={BrokerMs} totalMs={TotalMs}",
            request.ClientOrderId, result.BrokerOrderId, result.Success, result.HttpStatusCode, brokerAckMs, totalSw.ElapsedMilliseconds);
        await _audit.LogOrderResultAsync(result.Success, result.BrokerOrderId, result.ErrorMessage,
            result.HttpStatusCode, request.ClientOrderId, brokerAckMs);

        if (result.Success)
        {
            state.BrokerOrderId = result.BrokerOrderId;
            state.Transition(OrderStatus.Accepted);
            _activeOrders[result.BrokerOrderId!] = state;
            ToastRequested?.Invoke($"Order {request.Side} {request.Quantity} {request.Symbol} ACCEPTED ({brokerAckMs}ms)");
            _ = RefreshAcceptedOrderStatusAsync(state);
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
        var totalSw = Stopwatch.StartNew();
        _logger.LogInformation("[LATENCY] Cancel start account={Account} order={OrderId}", accountId, brokerOrderId);
        var state = ResolveActiveOrderState(brokerOrderId);
        if (state == null)
        {
            _logger.LogWarning("Cancel requested for unknown order id {OrderId}; issuing broker cancel as fallback", brokerOrderId);
            var fallback = await _broker.CancelOrderAsync(accountId, brokerOrderId);
            if (fallback.Success)
            {
                ToastRequested?.Invoke($"Order {brokerOrderId} cancel sent");
                return (true, "OK");
            }
            return (false, $"Order not found locally and broker cancel failed: {fallback.ErrorMessage}");
        }

        state.Transition(OrderStatus.CancelPending);
        OrderStateChanged?.Invoke(state);

        var brokerSw = Stopwatch.StartNew();
        var result = await _broker.CancelOrderAsync(accountId, brokerOrderId);
        _logger.LogInformation(
            "[LATENCY] Cancel broker-ack account={Account} order={OrderId} success={Success} http={HttpStatus} brokerMs={BrokerMs}",
            accountId, brokerOrderId, result.Success, result.HttpStatusCode, brokerSw.ElapsedMilliseconds);
        if (result.Success)
        {
            var confirmed = await WaitForCancelConfirmationAsync(accountId, brokerOrderId, state);
            if (confirmed)
            {
                _logger.LogInformation("Order {Id} cancelled", brokerOrderId);
            }
            else
            {
                state.Transition(OrderStatus.Working, "Cancel not confirmed by broker");
                _logger.LogWarning("Cancel request for {Id} was accepted but order is still working", brokerOrderId);
                result = OrderResult.Fail("Cancel request accepted but order is still working at broker");
            }
        }
        else
        {
            // Do not mark as cancelled if it failed — keep it as working
            state.Transition(
                IsTerminalBrokerFailure(result.ErrorMessage) ? OrderStatus.BrokerRejected : OrderStatus.Working,
                "Cancel failed: " + result.ErrorMessage);
            _logger.LogWarning("Cancel failed for {Id}: {Error}", brokerOrderId, result.ErrorMessage);
        }

        OrderStateChanged?.Invoke(state);
        _logger.LogInformation(
            "[LATENCY] Cancel completed account={Account} order={OrderId} success={Success} finalStatus={Status} totalMs={TotalMs}",
            accountId, brokerOrderId, result.Success, state.Status, totalSw.ElapsedMilliseconds);
        return (result.Success, result.ErrorMessage ?? "OK");
    }

    private async Task RefreshAcceptedOrderStatusAsync(OrderState state)
    {
        if (string.IsNullOrWhiteSpace(state.BrokerOrderId)) return;

        try
        {
            foreach (var delayMs in new[] { 500, 1500, 3000 })
            {
                await Task.Delay(delayMs);
                var live = await _broker.GetOrderStatusAsync(state.AccountId, state.BrokerOrderId);
                if (live == null) continue;

                state.QuantityFilled = live.QuantityFilled;
                state.AverageFillPrice = live.AverageFillPrice;
                state.Transition(live.Status, live.BrokerMessage);
                OrderStateChanged?.Invoke(state);

                if (live.IsTerminal || live.Status == OrderStatus.Working || live.Status == OrderStatus.PartiallyFilled)
                    return;
            }
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Post-accept status refresh failed for {OrderId}", state.BrokerOrderId);
        }
    }

    private async Task<bool> WaitForCancelConfirmationAsync(string accountId, string brokerOrderId, OrderState state)
    {
        for (var attempt = 0; attempt < 8; attempt++)
        {
            await Task.Delay(attempt == 0 ? 150 : 300);
            var live = await _broker.GetOrderStatusAsync(accountId, brokerOrderId);
            if (live == null)
            {
                state.Transition(OrderStatus.Cancelled);
                OrderStateChanged?.Invoke(state);
                return true;
            }

            state.QuantityFilled = live.QuantityFilled;
            state.AverageFillPrice = live.AverageFillPrice;
            state.Transition(live.Status, live.BrokerMessage);
            OrderStateChanged?.Invoke(state);

            if (live.Status == OrderStatus.CancelPending)
                continue;

            return live.IsTerminal;
        }

        return false;
    }

    private OrderState? ResolveActiveOrderState(string brokerOrderId)
    {
        if (_activeOrders.TryGetValue(brokerOrderId, out var state))
            return state;

        var fallback = _activeOrders.Values.FirstOrDefault(o =>
            string.Equals(o.ClientOrderId, brokerOrderId, StringComparison.Ordinal) ||
            string.Equals(o.BrokerOrderId, brokerOrderId, StringComparison.Ordinal));
        return fallback;
    }

    public async Task<(bool success, string message)> ReplaceOrderAsync(
        string accountId, OrderReplace replacement)
    {
        var totalSw = Stopwatch.StartNew();
        var oldBrokerId = replacement.BrokerOrderId;
        _logger.LogInformation(
            "[LATENCY] Replace start account={Account} order={OrderId} newLimit={NewLimit} newStop={NewStop} newQty={NewQty}",
            accountId, oldBrokerId, replacement.NewLimitPrice, replacement.NewStopPrice, replacement.NewQuantity);
        if (!_activeOrders.TryGetValue(oldBrokerId, out var state))
            return (false, "Order not found");

        state.Transition(OrderStatus.ReplacePending);
        OrderStateChanged?.Invoke(state);

        var brokerSw = Stopwatch.StartNew();
        var result = await _broker.ReplaceOrderAsync(accountId, replacement);
        _logger.LogInformation(
            "[LATENCY] Replace broker-ack account={Account} order={OrderId} newBroker={NewBrokerId} success={Success} http={HttpStatus} brokerMs={BrokerMs}",
            accountId, oldBrokerId, result.BrokerOrderId, result.Success, result.HttpStatusCode, brokerSw.ElapsedMilliseconds);
        if (result.Success)
        {
            if (replacement.NewLimitPrice.HasValue) state.LimitPrice = replacement.NewLimitPrice;
            if (replacement.NewStopPrice.HasValue) state.StopPrice = replacement.NewStopPrice;

            // Alpaca's PATCH creates a NEW order with a NEW broker id and marks
            // the old one Replaced. Schwab keeps the same id. If the broker gave
            // us a fresh id, re-key the state under it and drop the old key so
            // the stream update for the old id (status=replaced) doesn't hit us
            // and terminate the state we're still using.
            var newBrokerId = result.BrokerOrderId;
            if (!string.IsNullOrEmpty(newBrokerId) && newBrokerId != oldBrokerId)
            {
                state.BrokerOrderId = newBrokerId;
                _activeOrders[newBrokerId] = state;
                _activeOrders.Remove(oldBrokerId);
                _logger.LogInformation("Order re-keyed on replace: {Old} → {New}", oldBrokerId, newBrokerId);
            }

            state.Transition(OrderStatus.Working);
        }
        else
        {
            var syncedAfterFailure = false;
            var live = !string.IsNullOrWhiteSpace(state.BrokerOrderId)
                ? await _broker.GetOrderStatusAsync(accountId, state.BrokerOrderId)
                : null;

            if (live != null)
            {
                state.Transition(live.Status, "Replace failed: " + result.ErrorMessage);
                state.QuantityFilled = live.QuantityFilled;
                state.AverageFillPrice = live.AverageFillPrice;
            }
            else if (state.IsTerminal)
            {
                state.Transition(state.Status, "Replace failed: " + result.ErrorMessage);
            }
            else if (IsTerminalBrokerFailure(result.ErrorMessage))
            {
                state.Transition(OrderStatus.BrokerRejected, "Replace failed: " + result.ErrorMessage);
            }
            else
            {
                state.Transition(OrderStatus.Working, "Replace failed: " + result.ErrorMessage);
            }

            try
            {
                await SyncOrdersAsync(accountId);
                syncedAfterFailure = true;
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Order sync after failed replace failed for {Account}", accountId);
            }

            if (syncedAfterFailure)
            {
                _logger.LogInformation(
                    "[LATENCY] Replace completed account={Account} oldOrder={OldOrderId} currentOrder={CurrentOrderId} success={Success} finalStatus={Status} totalMs={TotalMs}",
                    accountId, oldBrokerId, state.BrokerOrderId, result.Success, state.Status, totalSw.ElapsedMilliseconds);
                return (false, result.ErrorMessage ?? "Replace failed; orders refreshed");
            }
        }

        OrderStateChanged?.Invoke(state);
        _logger.LogInformation(
            "[LATENCY] Replace completed account={Account} oldOrder={OldOrderId} currentOrder={CurrentOrderId} success={Success} finalStatus={Status} totalMs={TotalMs}",
            accountId, oldBrokerId, state.BrokerOrderId, result.Success, state.Status, totalSw.ElapsedMilliseconds);
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

    public async Task CancelAllForSymbolFastAsync(string accountId, string symbol)
    {
        // Fast flatten path: send all cancel requests concurrently and do not
        // wait for each order to poll into a terminal state. The normal sync
        // loop reconciles final status. This keeps the flatten exit from being
        // blocked behind several OCO/bracket cancel confirmations.
        var working = _activeOrders.Values
            .Where(o => o.AccountId == accountId && o.Symbol == symbol && o.IsWorking)
            .DistinctBy(o => o.BrokerOrderId ?? o.ClientOrderId)
            .Where(o => !string.IsNullOrWhiteSpace(o.BrokerOrderId))
            .ToList();

        _logger.LogInformation("FastCancelAll for {Symbol}: {Count} orders", symbol, working.Count);
        var tasks = working.Select(async o =>
        {
            var brokerOrderId = o.BrokerOrderId!;
            o.Transition(OrderStatus.CancelPending);
            OrderStateChanged?.Invoke(o);

            var sw = Stopwatch.StartNew();
            var result = await _broker.CancelOrderAsync(accountId, brokerOrderId);
            _logger.LogInformation(
                "[LATENCY] FastCancel broker-ack account={Account} order={OrderId} success={Success} http={HttpStatus} brokerMs={BrokerMs}",
                accountId, brokerOrderId, result.Success, result.HttpStatusCode, sw.ElapsedMilliseconds);

            if (!result.Success)
            {
                o.Transition(
                    IsTerminalBrokerFailure(result.ErrorMessage) ? OrderStatus.BrokerRejected : OrderStatus.Working,
                    "Fast cancel failed: " + result.ErrorMessage);
                OrderStateChanged?.Invoke(o);
            }
        });

        await Task.WhenAll(tasks);
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
        var sw = Stopwatch.StartNew();
        var orders = await _broker.SyncOrdersAsync(accountId);
        UpdateTodayRealizedPnL(accountId, orders);
        var workingCount = 0;
        foreach (var o in orders)
        {
            if (o.BrokerOrderId == null) continue;

            if (_activeOrders.TryGetValue(o.BrokerOrderId, out var existing))
            {
                var oldStatus = existing.Status;
                var oldFilled = existing.QuantityFilled;
                var oldAvg = existing.AverageFillPrice;
                var oldLimit = existing.LimitPrice;
                var oldStop = existing.StopPrice;
                existing.Transition(o.Status, o.BrokerMessage);
                existing.QuantityFilled = o.QuantityFilled;
                existing.AverageFillPrice = o.AverageFillPrice;
                existing.LimitPrice = o.LimitPrice;
                existing.StopPrice = o.StopPrice;

                var changed = oldStatus != existing.Status
                              || oldFilled != existing.QuantityFilled
                              || oldAvg != existing.AverageFillPrice
                              || oldLimit != existing.LimitPrice
                              || oldStop != existing.StopPrice;

                if (changed)
                    OrderStateChanged?.Invoke(existing);
            }
            else if (o.IsWorking)
            {
                _activeOrders[o.BrokerOrderId] = o;
                OrderStateChanged?.Invoke(o);
            }

            if (o.IsWorking) workingCount++;
        }
        _logger.LogInformation("Synced {Count} orders for {Account} ({Working} working)",
            orders.Count, accountId, workingCount);
        _logger.LogInformation(
            "[LATENCY] SyncOrders completed account={Account} count={Count} working={Working} totalMs={TotalMs}",
            accountId, orders.Count, workingCount, sw.ElapsedMilliseconds);
    }

    private void UpdateTodayRealizedPnL(string accountId, IReadOnlyList<OrderState> orders)
    {
        var etZone = TimeZoneInfo.FindSystemTimeZoneById("Eastern Standard Time");
        var todayEt = TimeZoneInfo.ConvertTimeFromUtc(DateTime.UtcNow, etZone).Date;
        var symbols = orders
            .Where(o => string.Equals(o.AccountId, accountId, StringComparison.OrdinalIgnoreCase))
            .Select(o => o.Symbol)
            .Where(s => !string.IsNullOrWhiteSpace(s))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        foreach (var symbol in symbols)
            _todayRealizedPnL.Remove(PnLKey(accountId, symbol));

        foreach (var group in orders
                     .Where(o => string.Equals(o.AccountId, accountId, StringComparison.OrdinalIgnoreCase)
                                 && TimeZoneInfo.ConvertTimeFromUtc(o.CreatedAtUtc, etZone).Date == todayEt
                                 && o.QuantityFilled > 0
                                 && o.AverageFillPrice.HasValue)
                     .GroupBy(o => o.Symbol, StringComparer.OrdinalIgnoreCase))
        {
            var pnl = ComputeRealizedPnL(group.OrderBy(o => o.CreatedAtUtc));
            _todayRealizedPnL[PnLKey(accountId, group.Key)] = pnl;
        }
    }

    private static decimal ComputeRealizedPnL(IEnumerable<OrderState> fills)
    {
        var lots = new List<(int Qty, decimal Price)>();
        decimal realized = 0m;

        foreach (var fill in fills)
        {
            var delta = fill.Side == OrderSide.Buy ? fill.QuantityFilled : -fill.QuantityFilled;
            var price = fill.AverageFillPrice!.Value;

            while (delta != 0 && lots.Count > 0 && Math.Sign(lots[0].Qty) != Math.Sign(delta))
            {
                var lot = lots[0];
                lots.RemoveAt(0);
                var closeQty = Math.Min(Math.Abs(lot.Qty), Math.Abs(delta));

                realized += lot.Qty > 0
                    ? (price - lot.Price) * closeQty
                    : (lot.Price - price) * closeQty;

                lot.Qty += lot.Qty > 0 ? -closeQty : closeQty;
                delta += delta > 0 ? -closeQty : closeQty;

                if (lot.Qty != 0)
                    lots.Insert(0, lot);
            }

            if (delta != 0)
                lots.Add((delta, price));
        }

        return realized;
    }

    private static string PnLKey(string accountId, string symbol) =>
        $"{accountId.Trim().ToUpperInvariant()}|{symbol.Trim().ToUpperInvariant()}";

    private static bool IsStopOrder(OrderType t) =>
        t is OrderType.StopMarket or OrderType.StopLimit;

    private static bool IsTerminalBrokerFailure(string? message)
    {
        if (string.IsNullOrWhiteSpace(message)) return false;
        return message.Contains("state REJECTED", StringComparison.OrdinalIgnoreCase)
               || message.Contains("cannot be canceled", StringComparison.OrdinalIgnoreCase)
               || message.Contains("already terminal", StringComparison.OrdinalIgnoreCase);
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
            // Guard against stale Replaced updates from Alpaca. Its PATCH-replace
            // marks the OLD order Replaced and the stream then emits that terminal
            // event with the SAME client_order_id — which would otherwise re-bind
            // our state to the old broker id and mark it terminal, erasing the
            // just-replaced live order from the DOM.
            if (update.Status == OrderStatus.Replaced &&
                !string.IsNullOrEmpty(byClient.BrokerOrderId) &&
                !string.IsNullOrEmpty(update.BrokerOrderId) &&
                byClient.BrokerOrderId != update.BrokerOrderId)
            {
                _logger.LogInformation(
                    "Ignoring stale Replaced update for old id {Old}; state now tracks {New}",
                    update.BrokerOrderId, byClient.BrokerOrderId);
                _ = _audit.LogOrderStateChangeAsync(update);
                return;
            }

            byClient.BrokerOrderId = update.BrokerOrderId;
            byClient.Transition(update.Status);
            if (update.BrokerOrderId != null)
                _activeOrders[update.BrokerOrderId] = byClient;
        }

        OrderStateChanged?.Invoke(update);
        _ = _audit.LogOrderStateChangeAsync(update);
    }
}
