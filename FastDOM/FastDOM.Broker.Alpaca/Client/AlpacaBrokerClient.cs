using System.Net.WebSockets;
using System.Reactive.Linq;
using System.Reactive.Subjects;
using System.Text;
using System.Text.Json;
using FastDOM.Broker.Interfaces;
using FastDOM.Core.Enums;
using FastDOM.Core.Models;
using FastDOM.Infrastructure.Config;
using Microsoft.Extensions.Logging;

namespace FastDOM.Broker.Alpaca.Client;

public class AlpacaBrokerClient : IBrokerClient
{
    private readonly ILogger<AlpacaBrokerClient> _logger;
    private readonly AlpacaConfig _config;
    private readonly HttpClient _http;
    private readonly Subject<OrderState> _orderSubject = new();
    private readonly Subject<bool> _connectionSubject = new();
    private ClientWebSocket? _tradeWs;
    private CancellationTokenSource? _tradeWsCts;
    private bool _connected;
    private bool _disposed;

    public bool IsConnected => _connected;
    public string BrokerName => _config.IsPaper ? "Alpaca Paper" : "Alpaca Live";
    public IObservable<OrderState> OrderUpdateStream => _orderSubject.AsObservable();
    public IObservable<bool> ConnectionStateStream => _connectionSubject.AsObservable();

    public AlpacaBrokerClient(ILogger<AlpacaBrokerClient> logger, AlpacaConfig config)
    {
        _logger = logger;
        _config = config;
        _http = new HttpClient();
        _http.DefaultRequestHeaders.Add("APCA-API-KEY-ID", config.ApiKey);
        _http.DefaultRequestHeaders.Add("APCA-API-SECRET-KEY", config.ApiSecret);
    }

    // ── Connection ────────────────────────────────────────────────────────────

    public async Task ConnectAsync(CancellationToken ct = default)
    {
        try
        {
            var resp = await _http.GetAsync($"{_config.TraderApiBase}/account", ct);
            resp.EnsureSuccessStatusCode();
            _connected = true;
            if (!_disposed) _connectionSubject.OnNext(true);
            _logger.LogInformation("Alpaca connected ({Mode})", BrokerName);

            // Start trade-update WebSocket in the background
            _ = Task.Run(() => RunTradeStreamAsync(ct), ct);
        }
        catch (Exception ex)
        {
            _connected = false;
            if (!_disposed) _connectionSubject.OnNext(false);
            _logger.LogError(ex, "Alpaca connection failed");
        }
    }

    public async Task DisconnectAsync(CancellationToken ct = default)
    {
        _tradeWsCts?.Cancel();
        if (_tradeWs?.State == WebSocketState.Open)
        {
            try { await _tradeWs.CloseAsync(WebSocketCloseStatus.NormalClosure, "", CancellationToken.None); }
            catch { /* best-effort */ }
        }
        _tradeWs?.Dispose();
        _tradeWs = null;
        _connected = false;
        if (!_disposed) _connectionSubject.OnNext(false);
    }

    // ── Trade-update WebSocket ────────────────────────────────────────────────

    private async Task RunTradeStreamAsync(CancellationToken ct)
    {
        _tradeWsCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var token = _tradeWsCts.Token;
        try
        {
            _tradeWs = new ClientWebSocket();
            await _tradeWs.ConnectAsync(new Uri(_config.StreamBase), token);

            // Authenticate
            await SendTradeWsAsync(JsonSerializer.Serialize(new
            {
                action = "authenticate",
                data   = new { key_id = _config.ApiKey, secret_key = _config.ApiSecret },
            }), token);

            // Wait briefly for auth confirmation, then subscribe
            await Task.Delay(500, token);
            await SendTradeWsAsync(JsonSerializer.Serialize(new
            {
                action = "listen",
                data   = new { streams = new[] { "trade_updates" } },
            }), token);

            _logger.LogInformation("Alpaca trade stream connected");
            await TradeReceiveLoopAsync(token);
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Alpaca trade stream error");
        }
    }

    private async Task TradeReceiveLoopAsync(CancellationToken ct)
    {
        var buffer = new byte[64 * 1024];
        while (!ct.IsCancellationRequested && _tradeWs?.State == WebSocketState.Open)
        {
            try
            {
                var sb = new StringBuilder();
                WebSocketReceiveResult result;
                do
                {
                    result = await _tradeWs.ReceiveAsync(new ArraySegment<byte>(buffer), ct);
                    if (result.MessageType == WebSocketMessageType.Close) return;
                    sb.Append(Encoding.UTF8.GetString(buffer, 0, result.Count));
                } while (!result.EndOfMessage);

                ProcessTradeMessage(sb.ToString());
            }
            catch (OperationCanceledException) { break; }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Trade stream receive error");
                break;
            }
        }
    }

    private void ProcessTradeMessage(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;

            // Alpaca wraps events as {"stream":"trade_updates","data":{...}}
            if (!root.TryGetProperty("stream", out var streamProp)) return;
            if (streamProp.GetString() != "trade_updates") return;
            if (!root.TryGetProperty("data", out var data)) return;

            var eventType = data.TryGetProperty("event", out var ev) ? ev.GetString() : "";
            if (!data.TryGetProperty("order", out var orderEl)) return;

            var state = MapTradeUpdateOrder(orderEl, eventType);
            if (state != null)
            {
                _logger.LogInformation("Trade update: {Event} {Symbol} {Status}", eventType, state.Symbol, state.Status);
                if (!_disposed) _orderSubject.OnNext(state);
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to parse trade update message");
        }
    }

    private static OrderState? MapTradeUpdateOrder(JsonElement e, string? eventType)
    {
        var symbol = e.TryGetProperty("symbol", out var sym) ? sym.GetString() ?? "" : "";
        if (string.IsNullOrEmpty(symbol)) return null;

        var brokerQty = ParseIntFromString(e, "qty");
        var filledQty = ParseIntFromString(e, "filled_qty");

        var state = new OrderState
        {
            ClientOrderId   = e.TryGetProperty("client_order_id", out var cid) ? cid.GetString() ?? "" : "",
            BrokerOrderId   = e.TryGetProperty("id", out var id) ? id.GetString() : null,
            AccountId       = "",
            Symbol          = symbol,
            Side            = e.TryGetProperty("side", out var side) && side.GetString() == "sell"
                                  ? OrderSide.Sell : OrderSide.Buy,
            QuantityOrdered = brokerQty,
            OrderType       = MapOrderTypeStr(e.TryGetProperty("type", out var ot) ? ot.GetString() : null),
            Source          = OrderSource.System,
            Status          = MapEventToStatus(eventType),
            LimitPrice      = ParseDecimalFromString(e, "limit_price"),
            StopPrice       = ParseDecimalFromString(e, "stop_price"),
        };
        state.QuantityFilled = filledQty;

        if (e.TryGetProperty("filled_avg_price", out var fap) && fap.ValueKind != JsonValueKind.Null)
        {
            if (decimal.TryParse(fap.GetString(), out var fapv)) state.AverageFillPrice = fapv;
        }

        return state;
    }

    private static OrderStatus MapEventToStatus(string? ev) => ev switch
    {
        "new"             => OrderStatus.Accepted,
        "pending_new"     => OrderStatus.Submitted,
        "fill"            => OrderStatus.Filled,
        "partial_fill"    => OrderStatus.PartiallyFilled,
        "canceled"        => OrderStatus.Cancelled,
        "expired"         => OrderStatus.Cancelled,
        "replaced"        => OrderStatus.Replaced,
        "pending_replace" => OrderStatus.ReplacePending,
        "pending_cancel"  => OrderStatus.CancelPending,
        "rejected"        => OrderStatus.BrokerRejected,
        "accepted"        => OrderStatus.Accepted,
        _                 => OrderStatus.Unknown,
    };

    // ── Account / Positions ───────────────────────────────────────────────────

    public async Task<IReadOnlyList<AccountInfo>> GetAccountsAsync(CancellationToken ct = default)
    {
        try
        {
            var resp = await _http.GetAsync($"{_config.TraderApiBase}/account", ct);
            resp.EnsureSuccessStatusCode();
            var body = await resp.Content.ReadAsStringAsync(ct);
            using var doc = JsonDocument.Parse(body);
            var root = doc.RootElement;
            var acctNum = root.TryGetProperty("account_number", out var anEl) ? anEl.GetString() ?? "" : "";
            var uuid    = root.TryGetProperty("id",             out var idEl) ? idEl.GetString() ?? "" : "";
            var accountId = string.IsNullOrEmpty(acctNum) ? uuid : acctNum;
            return [new AccountInfo { AccountId = accountId, AccountHash = uuid, DisplayName = accountId, AccountType = "Alpaca" }];
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "GetAccountsAsync failed");
            return [];
        }
    }

    public async Task<AccountSummary> GetAccountSummaryAsync(string accountId, CancellationToken ct = default)
    {
        try
        {
            var accTask = _http.GetAsync($"{_config.TraderApiBase}/account", ct);
            var posTask = _http.GetAsync($"{_config.TraderApiBase}/positions", ct);
            await Task.WhenAll(accTask, posTask);

            var accResp = await accTask;
            accResp.EnsureSuccessStatusCode();
            var accBody = await accResp.Content.ReadAsStringAsync(ct);
            using var accDoc = JsonDocument.Parse(accBody);
            var root = accDoc.RootElement;

            var summary = new AccountSummary
            {
                AccountId          = accountId,
                AccountName        = BrokerName,
                NetLiquidation     = ParseDecimal(root, "equity"),
                BuyingPower        = ParseDecimal(root, "buying_power"),
                DayTradingBuyingPower = ParseDecimal(root, "daytrading_buying_power"),
                DailyRealizedPnL   = ParseDecimal(root, "realized_pl"),
                DailyUnrealizedPnL = ParseDecimal(root, "unrealized_pl"),
            };

            // Positions
            var posResp = await posTask;
            if (posResp.IsSuccessStatusCode)
            {
                var posBody = await posResp.Content.ReadAsStringAsync(ct);
                using var posDoc = JsonDocument.Parse(posBody);
                foreach (var p in posDoc.RootElement.EnumerateArray())
                {
                    var posSym = p.TryGetProperty("symbol", out var s) ? s.GetString() ?? "" : "";
                    if (string.IsNullOrEmpty(posSym)) continue;
                    var qty = ParseIntFromString(p, "qty");
                    // Alpaca: positive qty = long, negative = short (but API returns signed int as string)
                    if (p.TryGetProperty("qty", out var qtyEl) && qtyEl.GetString()?.StartsWith("-") == true)
                        qty = -qty;
                    summary.Positions[posSym] = new Position
                    {
                        AccountId    = accountId,
                        Symbol       = posSym,
                        Quantity     = qty,
                        AverageCost  = ParseDecimalFromString(p, "avg_entry_price"),
                        CurrentPrice = ParseDecimalFromString(p, "current_price"),
                        UnrealizedPnL = ParseDecimalFromString(p, "unrealized_pl"),
                        DayPnL       = ParseDecimalFromString(p, "unrealized_intraday_pl"),
                        RealizedPnL   = ParseDecimalFromString(p, "realized_pl"),
                    };
                }
            }

            return summary;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "GetAccountSummaryAsync failed");
            return new AccountSummary { AccountId = accountId, AccountName = BrokerName };
        }
    }

    // ── Orders ────────────────────────────────────────────────────────────────

    public async Task<IReadOnlyList<OrderState>> GetOpenOrdersAsync(string accountId, CancellationToken ct = default)
    {
        try
        {
            var resp = await _http.GetAsync($"{_config.TraderApiBase}/orders?status=open&limit=500", ct);
            resp.EnsureSuccessStatusCode();
            var body = await resp.Content.ReadAsStringAsync(ct);
            using var doc = JsonDocument.Parse(body);
            return doc.RootElement.EnumerateArray()
                      .Select(e => MapOrder(e, accountId))
                      .Where(o => o != null).Cast<OrderState>()
                      .ToList();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "GetOpenOrdersAsync failed");
            return [];
        }
    }

    public async Task<OrderResult> PlaceOrderAsync(OrderRequest request, CancellationToken ct = default)
    {
        try
        {
            var payload = new
            {
                symbol        = request.Symbol,
                qty           = request.Quantity.ToString(),
                side          = request.Side == OrderSide.Buy ? "buy" : "sell",
                type          = MapOrderType(request.OrderType),
                time_in_force = MapTif(request.TimeInForce),
                limit_price   = request.LimitPrice.HasValue && request.LimitPrice > 0
                                    ? request.LimitPrice.Value.ToString("F2") : null,
                stop_price    = request.StopPrice.HasValue && request.StopPrice > 0
                                    ? request.StopPrice.Value.ToString("F2") : null,
                client_order_id = request.ClientOrderId,
                extended_hours = request.ExtendedHours,
            };

            var json = JsonSerializer.Serialize(payload, new JsonSerializerOptions
            {
                DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull
            });
            var content = new StringContent(json, Encoding.UTF8, "application/json");
            var resp = await _http.PostAsync($"{_config.TraderApiBase}/orders", content, ct);
            var body = await resp.Content.ReadAsStringAsync(ct);

            if (!resp.IsSuccessStatusCode)
            {
                _logger.LogError("Alpaca PlaceOrder failed: {Status} {Body}", resp.StatusCode, body);
                return OrderResult.Fail($"Alpaca rejected: {resp.StatusCode} — {InterpretAlpacaError(body)}");
            }

            using var doc = JsonDocument.Parse(body);
            var orderId = doc.RootElement.TryGetProperty("id", out var idEl) ? idEl.GetString() ?? "" : "";
            _logger.LogInformation("Alpaca order placed: {Id}", orderId);
            return OrderResult.Ok(orderId, request.ClientOrderId);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "PlaceOrderAsync failed");
            return OrderResult.Fail(ex.Message);
        }
    }

    // Translate Alpaca's raw JSON error body into a trader-friendly message.
    // Wash-trade rejections (code 40310000) return the CONFLICTING order's fields
    // (e.g. buy_limit_price) — surface that as "existing X @ Y blocks Z" so the user
    // doesn't misread it as the submitted order being wrong-side.
    public static string InterpretAlpacaError(string body)
    {
        try
        {
            using var doc = JsonDocument.Parse(body);
            var root = doc.RootElement;
            if (root.TryGetProperty("code", out var codeEl) && codeEl.ValueKind == JsonValueKind.Number)
            {
                int code = codeEl.GetInt32();
                if (code == 40310000)
                {
                    var side  = root.TryGetProperty("buy_limit_price", out var b) ? ("Buy Limit @ " + b.GetString())
                              : root.TryGetProperty("sell_limit_price", out var s) ? ("Sell Limit @ " + s.GetString())
                              : "opposite-side order";
                    return $"Wash-trade prevention: existing {side} blocks this submission — cancel the existing order first.";
                }
                if (code == 42210000)
                    return "Order still in 'accepted' state at Alpaca (not yet routed to venue). Wait a moment and try again — extended-hours orders often sit here until the session opens.";
            }
            if (root.TryGetProperty("message", out var m)) return m.GetString() ?? body;
        }
        catch { }
        return body;
    }

    // Was this Alpaca error a transient "not yet routable" reject that a short
    // retry should get past? Used by ReplaceOrderAsync to auto-retry.
    private static bool IsTransientReplaceError(string body)
    {
        try
        {
            using var doc = JsonDocument.Parse(body);
            if (doc.RootElement.TryGetProperty("code", out var codeEl) &&
                codeEl.ValueKind == JsonValueKind.Number)
            {
                return codeEl.GetInt32() == 42210000;
            }
        }
        catch { }
        return false;
    }

    public async Task<OrderResult> CancelOrderAsync(string accountId, string brokerOrderId, CancellationToken ct = default)
    {
        try
        {
            var resp = await _http.DeleteAsync($"{_config.TraderApiBase}/orders/{brokerOrderId}", ct);
            if (!resp.IsSuccessStatusCode)
            {
                var body = await resp.Content.ReadAsStringAsync(ct);
                return OrderResult.Fail($"Cancel failed: {resp.StatusCode} — {body}");
            }
            return OrderResult.Ok(brokerOrderId);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "CancelOrderAsync failed");
            return OrderResult.Fail(ex.Message);
        }
    }

    public async Task<OrderResult> ReplaceOrderAsync(string accountId, OrderReplace replacement, CancellationToken ct = default)
    {
        // 1. Try PATCH — the normal case. One quick retry for genuinely
        //    transient 42210000 errors (order in flight between accepted → new).
        //    Returns both the OrderResult and the raw body so we can classify
        //    the failure by Alpaca's code directly (robust to InterpretAlpacaError
        //    rephrasing the message).
        var (patchResult, rawBody) = await TryPatchAsync(replacement, ct);
        if (patchResult.Success) return patchResult;

        if (IsAcceptedStateError(rawBody))
        {
            _logger.LogInformation("Alpaca PATCH rejected as transient (42210000); retrying after 500ms");
            await Task.Delay(500, ct);
            (patchResult, rawBody) = await TryPatchAsync(replacement, ct);
            if (patchResult.Success) return patchResult;
        }

        // 2. If Alpaca is still refusing (order stuck in 'accepted' because it's
        //    an extended-hours order held during regular hours, or paper-account
        //    routing delay), fall back to cancel + place-new. Preserves user
        //    intent for any state that permits cancel.
        if (IsAcceptedStateError(rawBody))
        {
            _logger.LogInformation("Alpaca PATCH blocked (accepted state); falling back to cancel+place");
            return await CancelAndPlaceReplacementAsync(replacement, ct);
        }

        return patchResult;
    }

    // Classify a raw error body by Alpaca's numeric code — stable across
    // InterpretAlpacaError message rewording.
    private static bool IsAcceptedStateError(string rawBody)
    {
        try
        {
            using var doc = JsonDocument.Parse(rawBody);
            if (doc.RootElement.TryGetProperty("code", out var codeEl) &&
                codeEl.ValueKind == JsonValueKind.Number)
            {
                return codeEl.GetInt32() == 42210000;
            }
        }
        catch { }
        return false;
    }

    private async Task<(OrderResult result, string rawBody)> TryPatchAsync(OrderReplace replacement, CancellationToken ct)
    {
        try
        {
            var payload = new
            {
                qty         = replacement.NewQuantity?.ToString(),
                limit_price = replacement.NewLimitPrice?.ToString("F2"),
                stop_price  = replacement.NewStopPrice?.ToString("F2"),
            };
            var json = JsonSerializer.Serialize(payload, new JsonSerializerOptions
            {
                DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull
            });
            var req = new HttpRequestMessage(HttpMethod.Patch, $"{_config.TraderApiBase}/orders/{replacement.BrokerOrderId}")
            {
                Content = new StringContent(json, Encoding.UTF8, "application/json")
            };
            var resp = await _http.SendAsync(req, ct);
            var body = await resp.Content.ReadAsStringAsync(ct);
            if (!resp.IsSuccessStatusCode)
            {
                return (OrderResult.Fail($"Replace failed: {resp.StatusCode} — {InterpretAlpacaError(body)}"), body);
            }
            using var doc = JsonDocument.Parse(body);
            var newId = doc.RootElement.TryGetProperty("id", out var idEl) ? idEl.GetString() ?? "" : "";
            return (OrderResult.Ok(newId, replacement.OriginalClientOrderId), body);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "TryPatchAsync failed");
            return (OrderResult.Fail(ex.Message), string.Empty);
        }
    }

    // Fallback used when PATCH-replace is not permitted by Alpaca. Cancels the
    // original order, then places a new order carrying over side/type/tif/etc.
    // with the drag's new price and (optionally) new quantity.
    private async Task<OrderResult> CancelAndPlaceReplacementAsync(OrderReplace replacement, CancellationToken ct)
    {
        // Fetch original order so we know what to re-place.
        JsonElement orig;
        {
            var getReq = new HttpRequestMessage(HttpMethod.Get, $"{_config.TraderApiBase}/orders/{replacement.BrokerOrderId}");
            var getResp = await _http.SendAsync(getReq, ct);
            if (!getResp.IsSuccessStatusCode)
            {
                var b = await getResp.Content.ReadAsStringAsync(ct);
                return OrderResult.Fail($"Cancel+place fallback: GET failed {getResp.StatusCode} — {b}");
            }
            var getBody = await getResp.Content.ReadAsStringAsync(ct);
            using var doc = JsonDocument.Parse(getBody);
            orig = doc.RootElement.Clone();
        }

        // Cancel the original.
        var cancelResp = await _http.DeleteAsync($"{_config.TraderApiBase}/orders/{replacement.BrokerOrderId}", ct);
        // Alpaca returns 204 No Content on success; 422 if already terminal.
        // We treat both as "old order is out of the way" and continue.
        if (!cancelResp.IsSuccessStatusCode && (int)cancelResp.StatusCode != 422)
        {
            var b = await cancelResp.Content.ReadAsStringAsync(ct);
            return OrderResult.Fail($"Cancel+place fallback: cancel failed {cancelResp.StatusCode} — {b}");
        }

        // Small pause so Alpaca's state settles before the new POST.
        await Task.Delay(150, ct);

        // Rebuild the payload from the original order + replacement overrides.
        string? Str(JsonElement el, string name)
            => el.TryGetProperty(name, out var v) && v.ValueKind != JsonValueKind.Null ? v.GetString() : null;

        var newClientOrderId = (Str(orig, "client_order_id") ?? Guid.NewGuid().ToString("N")) + "-r";
        var payload = new
        {
            symbol          = Str(orig, "symbol"),
            qty             = replacement.NewQuantity?.ToString() ?? Str(orig, "qty"),
            side            = Str(orig, "side"),
            type            = Str(orig, "order_type") ?? Str(orig, "type"),
            time_in_force   = Str(orig, "time_in_force"),
            limit_price     = replacement.NewLimitPrice?.ToString("F2") ?? Str(orig, "limit_price"),
            stop_price      = replacement.NewStopPrice?.ToString("F2")  ?? Str(orig, "stop_price"),
            client_order_id = newClientOrderId,
            extended_hours  = orig.TryGetProperty("extended_hours", out var ehEl) && ehEl.ValueKind == JsonValueKind.True,
        };
        var json = JsonSerializer.Serialize(payload, new JsonSerializerOptions
        {
            DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull
        });
        var placeResp = await _http.PostAsync($"{_config.TraderApiBase}/orders",
                                              new StringContent(json, Encoding.UTF8, "application/json"), ct);
        var placeBody = await placeResp.Content.ReadAsStringAsync(ct);
        if (!placeResp.IsSuccessStatusCode)
            return OrderResult.Fail($"Cancel+place fallback: place failed {placeResp.StatusCode} — {InterpretAlpacaError(placeBody)}");

        using var placeDoc = JsonDocument.Parse(placeBody);
        var newId = placeDoc.RootElement.TryGetProperty("id", out var idEl) ? idEl.GetString() ?? "" : "";
        _logger.LogInformation("Cancel+place fallback succeeded: new order {NewId}", newId);
        return OrderResult.Ok(newId, replacement.OriginalClientOrderId);
    }

    public async Task<OrderState?> GetOrderStatusAsync(string accountId, string brokerOrderId, CancellationToken ct = default)
    {
        try
        {
            var resp = await _http.GetAsync($"{_config.TraderApiBase}/orders/{brokerOrderId}", ct);
            if (!resp.IsSuccessStatusCode) return null;
            var body = await resp.Content.ReadAsStringAsync(ct);
            using var doc = JsonDocument.Parse(body);
            return MapOrder(doc.RootElement, accountId);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "GetOrderStatusAsync failed");
            return null;
        }
    }

    public async Task<IReadOnlyList<OrderState>> SyncOrdersAsync(string accountId, CancellationToken ct = default)
    {
        try
        {
            // Fetch open + recently closed orders
            var resp = await _http.GetAsync(
                $"{_config.TraderApiBase}/orders?status=all&limit=200&direction=desc", ct);
            resp.EnsureSuccessStatusCode();
            var body = await resp.Content.ReadAsStringAsync(ct);
            using var doc = JsonDocument.Parse(body);
            return doc.RootElement.EnumerateArray()
                      .Select(e => MapOrder(e, accountId))
                      .Where(o => o != null).Cast<OrderState>()
                      .ToList();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "SyncOrdersAsync failed");
            return [];
        }
    }

    // ── Mapping helpers ───────────────────────────────────────────────────────

    private static OrderState? MapOrder(JsonElement e, string accountId)
    {
        var symbol = e.TryGetProperty("symbol", out var sym) ? sym.GetString() ?? "" : "";
        if (string.IsNullOrEmpty(symbol)) return null;

        var state = new OrderState
        {
            ClientOrderId   = e.TryGetProperty("client_order_id", out var cid) ? cid.GetString() ?? "" : "",
            BrokerOrderId   = e.TryGetProperty("id", out var id) ? id.GetString() : null,
            AccountId       = accountId,
            Symbol          = symbol,
            Side            = e.TryGetProperty("side", out var side) && side.GetString() == "sell"
                                  ? OrderSide.Sell : OrderSide.Buy,
            QuantityOrdered = ParseIntFromString(e, "qty"),
            OrderType       = MapOrderTypeStr(e.TryGetProperty("type", out var ot) ? ot.GetString() : null),
            Source          = OrderSource.System,
            Status          = MapStatus(e.TryGetProperty("status", out var st) ? st.GetString() : null),
            LimitPrice      = ParseDecimalFromString(e, "limit_price"),
            StopPrice       = ParseDecimalFromString(e, "stop_price"),
        };
        state.QuantityFilled = ParseIntFromString(e, "filled_qty");
        if (e.TryGetProperty("filled_avg_price", out var fap) && fap.ValueKind != JsonValueKind.Null)
        {
            if (decimal.TryParse(fap.GetString(), out var fapv)) state.AverageFillPrice = fapv;
        }
        return state;
    }

    private static OrderStatus MapStatus(string? s) => s switch
    {
        "new" or "accepted" or "pending_new" => OrderStatus.Accepted,
        "partially_filled"                   => OrderStatus.PartiallyFilled,
        "filled"                             => OrderStatus.Filled,
        "done_for_day" or "canceled"         => OrderStatus.Cancelled,
        "replaced"                           => OrderStatus.Replaced,
        "pending_cancel"                     => OrderStatus.CancelPending,
        "pending_replace"                    => OrderStatus.ReplacePending,
        "rejected"                           => OrderStatus.BrokerRejected,
        _                                    => OrderStatus.Unknown,
    };

    private static OrderType MapOrderTypeStr(string? t) => t switch
    {
        "limit"      => OrderType.Limit,
        "stop"       => OrderType.StopMarket,
        "stop_limit" => OrderType.StopLimit,
        _            => OrderType.Market,
    };

    private static string MapOrderType(OrderType t) => t switch
    {
        OrderType.Limit           => "limit",
        OrderType.StopMarket      => "stop",
        OrderType.StopLimit       => "stop_limit",
        OrderType.MarketableLimit => "limit",
        _                         => "market",
    };

    private static string MapTif(TimeInForce tif) => tif switch
    {
        TimeInForce.GTC => "gtc",
        TimeInForce.IOC => "ioc",
        TimeInForce.FOK => "fok",
        _               => "day",
    };

    private static decimal ParseDecimal(JsonElement root, string key)
    {
        if (!root.TryGetProperty(key, out var p)) return 0;
        if (p.ValueKind == JsonValueKind.Number) return p.GetDecimal();
        if (p.ValueKind == JsonValueKind.String && decimal.TryParse(p.GetString(), out var v)) return v;
        return 0;
    }

    private static decimal ParseDecimalFromString(JsonElement e, string key)
    {
        if (!e.TryGetProperty(key, out var p) || p.ValueKind == JsonValueKind.Null) return 0;
        if (p.ValueKind == JsonValueKind.Number) return p.GetDecimal();
        return decimal.TryParse(p.GetString(), out var v) ? v : 0;
    }

    private static int ParseIntFromString(JsonElement e, string key)
    {
        if (!e.TryGetProperty(key, out var p) || p.ValueKind == JsonValueKind.Null) return 0;
        if (p.ValueKind == JsonValueKind.Number) return p.GetInt32();
        var s = p.GetString()?.TrimStart('-') ?? "";
        return int.TryParse(s, out var v) ? v : 0;
    }

    private async Task SendTradeWsAsync(string message, CancellationToken ct)
    {
        if (_tradeWs?.State != WebSocketState.Open) return;
        var bytes = Encoding.UTF8.GetBytes(message);
        await _tradeWs.SendAsync(new ArraySegment<byte>(bytes), WebSocketMessageType.Text, true, ct);
    }

    // ── Disposal ──────────────────────────────────────────────────────────────

    public async ValueTask DisposeAsync()
    {
        _disposed = true;
        if (_connected) await DisconnectAsync();
        _orderSubject.Dispose();
        _connectionSubject.Dispose();
        _http.Dispose();
    }
}
