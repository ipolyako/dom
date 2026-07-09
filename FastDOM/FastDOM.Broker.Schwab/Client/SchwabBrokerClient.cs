using System.Net.Http.Headers;
using System.Reactive.Linq;
using System.Reactive.Subjects;
using System.Text;
using System.Text.Json;
using FastDOM.Broker.Interfaces;
using FastDOM.Broker.Schwab.Auth;
using FastDOM.Broker.Schwab.Mapping;
using FastDOM.Core.Enums;
using FastDOM.Core.Models;
using FastDOM.Infrastructure.Config;
using Microsoft.Extensions.Logging;

namespace FastDOM.Broker.Schwab.Client;

/// <summary>
/// Schwab Trader API broker client.
///
/// Confirmed endpoints (developer.schwab.com):
///   GET  /trader/v1/accounts/accountNumbers   → encrypted account hashes
///   GET  /trader/v1/accounts/{hash}?fields=positions
///   GET  /trader/v1/accounts/{hash}/orders
///   POST /trader/v1/accounts/{hash}/orders    → 201, orderId in Location header
///   PUT  /trader/v1/accounts/{hash}/orders/{id}
///   DELETE /trader/v1/accounts/{hash}/orders/{id}
/// </summary>
public class SchwabBrokerClient : IBrokerClient
{
    private readonly ILogger<SchwabBrokerClient> _logger;
    private readonly SchwabConfig _config;
    private readonly SchwabAuthProvider _auth;
    private readonly SchwabOrderMapper _mapper;
    private readonly HttpClient _http;
    private readonly Subject<OrderState> _orderSubject = new();
    private readonly Subject<bool> _connectionSubject = new();
    private readonly Dictionary<string, string> _accountHashes = []; // accountId → accountHash
    private bool _connected;

    public bool IsConnected => _connected;
    public string BrokerName => "Schwab";
    public IObservable<OrderState> OrderUpdateStream => _orderSubject.AsObservable();
    public IObservable<bool> ConnectionStateStream => _connectionSubject.AsObservable();

    public SchwabBrokerClient(
        ILogger<SchwabBrokerClient> logger,
        SchwabConfig config,
        SchwabAuthProvider auth,
        SchwabOrderMapper mapper)
    {
        _logger = logger;
        _config = config;
        _auth = auth;
        _mapper = mapper;
        _http = new HttpClient();
        _http.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
    }

    public async Task ConnectAsync(CancellationToken ct = default)
    {
        var token = await _auth.GetAccessTokenAsync(ct);
        if (string.IsNullOrEmpty(token))
        {
            _logger.LogError("Schwab connect failed: no valid access token");
            return;
        }

        // Verify connectivity by fetching account numbers
        var accounts = await GetAccountsAsync(ct);
        _connected = accounts.Count > 0;
        _connectionSubject.OnNext(_connected);

        if (_connected)
            _logger.LogInformation("Schwab connected. {Count} account(s) found.", accounts.Count);
        else
            _logger.LogWarning("Schwab: no accounts returned or auth failed");
    }

    public Task DisconnectAsync(CancellationToken ct = default)
    {
        _connected = false;
        _connectionSubject.OnNext(false);
        return Task.CompletedTask;
    }

    public async Task<IReadOnlyList<AccountInfo>> GetAccountsAsync(CancellationToken ct = default)
    {
        var resp = await GetAsync("/accounts/accountNumbers", ct);
        if (resp == null) return [];

        try
        {
            var list = new List<AccountInfo>();
            foreach (var item in resp.Value.EnumerateArray())
            {
                var accountId = item.GetProperty("accountNumber").GetString() ?? "";
                var hash = item.GetProperty("hashValue").GetString() ?? "";
                _accountHashes[accountId] = hash;
                list.Add(new AccountInfo
                {
                    AccountId    = accountId,
                    AccountHash  = hash,
                    DisplayName  = accountId,
                    AccountType  = null
                });
            }
            return list;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to parse account numbers response");
            return [];
        }
    }

    public async Task<AccountSummary> GetAccountSummaryAsync(string accountId, CancellationToken ct = default)
    {
        var hash = GetHash(accountId);
        var resp = await GetAsync($"/accounts/{hash}?fields=positions", ct);
        if (resp == null) return new AccountSummary { AccountId = accountId, AccountName = accountId };

        try
        {
            return ParseAccountSummary(accountId, resp.Value);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to parse account summary for {AccountId}", accountId);
            return new AccountSummary { AccountId = accountId, AccountName = accountId };
        }
    }

    public async Task<IReadOnlyList<OrderState>> GetOpenOrdersAsync(string accountId, CancellationToken ct = default)
    {
        var hash = GetHash(accountId);
        var resp = await GetAsync($"/accounts/{hash}/orders?status=WORKING", ct);
        if (resp == null) return [];

        try
        {
            return ParseOrders(accountId, resp.Value);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to parse orders for {AccountId}", accountId);
            return [];
        }
    }

    public async Task<OrderResult> PlaceOrderAsync(OrderRequest request, CancellationToken ct = default)
    {
        var token = await _auth.GetAccessTokenAsync(ct);
        if (string.IsNullOrEmpty(token))
            return OrderResult.Fail("Not authenticated");

        var hash = GetHash(request.AccountId);
        var json = _mapper.MapToJson(request);

        _logger.LogDebug("Schwab PlaceOrder: {Json}", json);

        using var req = new HttpRequestMessage(HttpMethod.Post, $"{_config.TraderApiBase}/accounts/{hash}/orders");
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        req.Content = new StringContent(json, Encoding.UTF8, "application/json");

        try
        {
            var resp = await _http.SendAsync(req, ct);
            if (resp.StatusCode == System.Net.HttpStatusCode.Created)
            {
                // Order ID is in the Location header
                var location = resp.Headers.Location?.ToString() ?? "";
                var orderId = location.Split('/').LastOrDefault() ?? "";
                _logger.LogInformation("Schwab order placed: {OrderId}", orderId);
                return OrderResult.Ok(orderId, request.ClientOrderId);
            }

            var body = await resp.Content.ReadAsStringAsync(ct);
            _logger.LogError("Schwab PlaceOrder failed: {Status} {Body}", resp.StatusCode, body);
            return OrderResult.Fail(body, (int)resp.StatusCode);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Schwab PlaceOrder exception");
            return OrderResult.Fail(ex.Message);
        }
    }

    public async Task<OrderResult> CancelOrderAsync(string accountId, string brokerOrderId, CancellationToken ct = default)
    {
        var token = await _auth.GetAccessTokenAsync(ct);
        if (string.IsNullOrEmpty(token)) return OrderResult.Fail("Not authenticated");

        var hash = GetHash(accountId);
        using var req = new HttpRequestMessage(HttpMethod.Delete,
            $"{_config.TraderApiBase}/accounts/{hash}/orders/{brokerOrderId}");
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);

        try
        {
            var resp = await _http.SendAsync(req, ct);
            if (resp.IsSuccessStatusCode)
                return OrderResult.Ok(brokerOrderId);

            // Schwab may return NotFound if the order was already filled or
            // canceled in another terminal state.
            if (resp.StatusCode == System.Net.HttpStatusCode.NotFound)
            {
                await resp.Content.ReadAsStringAsync(ct);
                _logger.LogInformation("Schwab cancel returned NotFound for {OrderId}; treating as already terminal", brokerOrderId);
                return OrderResult.Ok(brokerOrderId);
            }

            var body = await resp.Content.ReadAsStringAsync(ct);
            return OrderResult.Fail(body, (int)resp.StatusCode);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Schwab CancelOrder exception");
            return OrderResult.Fail(ex.Message);
        }
    }

    public async Task<OrderResult> ReplaceOrderAsync(string accountId, OrderReplace replacement, CancellationToken ct = default)
    {
        var token = await _auth.GetAccessTokenAsync(ct);
        if (string.IsNullOrEmpty(token)) return OrderResult.Fail("Not authenticated");

        // 1. Try PUT — the normal case.
        var (putResult, putStatus) = await TryPutReplaceAsync(accountId, replacement, token, ct);
        if (putResult.Success) return putResult;
        if (putStatus == 401 || putStatus == 403)
            return putResult;

        // Schwab expects a full replacement order on some accounts/order states.
        // If the minimal price-only PUT is rejected, retry with the original
        // order payload plus the requested price/quantity deltas.
        var (fullPutResult, fullPutStatus) = await TryFullPutReplaceAsync(accountId, replacement, token, ct);
        if (fullPutResult.Success) return fullPutResult;

        // 2. Fall back to cancel + place-new for any state error where a
        //    replace is refused but a cancel would still succeed (mirrors
        //    the Alpaca 42210000 handling). 4xx from Schwab typically means
        //    "this order isn't in a replaceable state right now" — cancel is
        //    always safe to try. Skip fallback for 401/403 (auth) since it
        //    won't help there.
        if (fullPutStatus == 401 || fullPutStatus == 403)
            return fullPutResult;

        _logger.LogInformation(
            "Schwab PUT replace failed (minimal HTTP {MinimalStatus}, full HTTP {FullStatus}); falling back to cancel+place",
            putStatus, fullPutStatus);
        return await CancelAndPlaceReplacementAsync(accountId, replacement, token, ct);
    }

    private async Task<(OrderResult result, int status)> TryPutReplaceAsync(
        string accountId, OrderReplace replacement, string token, CancellationToken ct)
    {
        var hash = GetHash(accountId);
        var body = new Dictionary<string, object>();
        if (replacement.NewLimitPrice.HasValue) body["price"] = replacement.NewLimitPrice.Value.ToString("F2");
        if (replacement.NewStopPrice.HasValue)  body["stopPrice"] = replacement.NewStopPrice.Value.ToString("F2");
        if (replacement.NewQuantity.HasValue)
        {
            body["orderLegCollection"] = new[] {
                new Dictionary<string, object> { ["quantity"] = replacement.NewQuantity.Value }
            };
        }
        var json = JsonSerializer.Serialize(body);

        using var req = new HttpRequestMessage(HttpMethod.Put,
            $"{_config.TraderApiBase}/accounts/{hash}/orders/{replacement.BrokerOrderId}");
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        req.Content = new StringContent(json, Encoding.UTF8, "application/json");

        try
        {
            var resp = await _http.SendAsync(req, ct);
            if (resp.IsSuccessStatusCode)
            {
                // 201 Created with Location: .../orders/{NEW_ID}. Old order becomes REPLACED.
                var newId = ExtractOrderIdFromLocation(resp.Headers.Location) ?? replacement.BrokerOrderId;
                return (OrderResult.Ok(newId), (int)resp.StatusCode);
            }
            var responseBody = await resp.Content.ReadAsStringAsync(ct);
            return (OrderResult.Fail(responseBody, (int)resp.StatusCode), (int)resp.StatusCode);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Schwab TryPutReplaceAsync exception");
            return (OrderResult.Fail(ex.Message), 0);
        }
    }

    private async Task<(OrderResult result, int status)> TryFullPutReplaceAsync(
        string accountId, OrderReplace replacement, string token, CancellationToken ct)
    {
        var hash = GetHash(accountId);
        var (orig, error, status) = await GetOriginalOrderAsync(hash, replacement.BrokerOrderId, token, ct);
        if (orig == null)
            return (OrderResult.Fail($"Full PUT replace: GET failed {status} — {error}", status), status);

        var placeBody = BuildReplacementPayload(orig.Value, replacement);
        var json = JsonSerializer.Serialize(placeBody);

        using var req = new HttpRequestMessage(HttpMethod.Put,
            $"{_config.TraderApiBase}/accounts/{hash}/orders/{replacement.BrokerOrderId}");
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        req.Content = new StringContent(json, Encoding.UTF8, "application/json");

        try
        {
            var resp = await _http.SendAsync(req, ct);
            if (resp.IsSuccessStatusCode)
            {
                var newId = ExtractOrderIdFromLocation(resp.Headers.Location) ?? replacement.BrokerOrderId;
                return (OrderResult.Ok(newId), (int)resp.StatusCode);
            }

            var responseBody = await resp.Content.ReadAsStringAsync(ct);
            return (OrderResult.Fail(responseBody, (int)resp.StatusCode), (int)resp.StatusCode);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Schwab TryFullPutReplaceAsync exception");
            return (OrderResult.Fail(ex.Message), 0);
        }
    }

    // Fallback for any Schwab state that refuses a PUT-replace. Fetches the
    // original order, cancels it, then places a fresh order carrying over the
    // side / order type / session / duration / instrument and overriding
    // price / stopPrice / quantity from the replacement.
    private async Task<OrderResult> CancelAndPlaceReplacementAsync(
        string accountId, OrderReplace replacement, string token, CancellationToken ct)
    {
        var hash = GetHash(accountId);

        // 1. GET original.
        var (orig, error, status) = await GetOriginalOrderAsync(hash, replacement.BrokerOrderId, token, ct);
        if (orig == null)
            return OrderResult.Fail($"Cancel+place fallback: GET failed {status} — {error}", status);

        // 2. Build new order payload from original + replacement deltas.
        var placeBody = BuildReplacementPayload(orig.Value, replacement);

        // 3. DELETE old. Treat 409/422 (already terminal) as "out of the way".
        using var delReq = new HttpRequestMessage(HttpMethod.Delete,
            $"{_config.TraderApiBase}/accounts/{hash}/orders/{replacement.BrokerOrderId}");
        delReq.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        var delResp = await _http.SendAsync(delReq, ct);
        var delCode = (int)delResp.StatusCode;
        if (!delResp.IsSuccessStatusCode && delCode != 409 && delCode != 422)
        {
            var b = await delResp.Content.ReadAsStringAsync(ct);
            return OrderResult.Fail($"Cancel+place fallback: DELETE failed {delCode} — {b}");
        }
        await Task.Delay(150, ct);

        // 4. POST new.
        var placeJson = JsonSerializer.Serialize(placeBody);
        using var postReq = new HttpRequestMessage(HttpMethod.Post,
            $"{_config.TraderApiBase}/accounts/{hash}/orders");
        postReq.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        postReq.Content = new StringContent(placeJson, Encoding.UTF8, "application/json");
        var postResp = await _http.SendAsync(postReq, ct);
        var postCode = (int)postResp.StatusCode;
        if (postResp.StatusCode == System.Net.HttpStatusCode.Created)
        {
            var newId = ExtractOrderIdFromLocation(postResp.Headers.Location) ?? "";
            _logger.LogInformation("Schwab cancel+place fallback succeeded: new order {NewId}", newId);
            return OrderResult.Ok(newId);
        }
        var postBody = await postResp.Content.ReadAsStringAsync(ct);
        return OrderResult.Fail($"Cancel+place fallback: POST failed {postCode} — {postBody}", postCode);
    }

    private async Task<(JsonElement? order, string error, int status)> GetOriginalOrderAsync(
        string accountHash, string brokerOrderId, string token, CancellationToken ct)
    {
        using var getReq = new HttpRequestMessage(HttpMethod.Get,
            $"{_config.TraderApiBase}/accounts/{accountHash}/orders/{brokerOrderId}");
        getReq.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        var getResp = await _http.SendAsync(getReq, ct);
        var getBody = await getResp.Content.ReadAsStringAsync(ct);
        if (!getResp.IsSuccessStatusCode)
            return (null, getBody, (int)getResp.StatusCode);

        using var doc = JsonDocument.Parse(getBody);
        return (doc.RootElement.Clone(), "", (int)getResp.StatusCode);
    }

    private static Dictionary<string, object> BuildReplacementPayload(JsonElement orig, OrderReplace replacement)
    {
        var placeBody = new Dictionary<string, object>();
        if (orig.TryGetProperty("orderType", out var otEl) && otEl.ValueKind == JsonValueKind.String)
            placeBody["orderType"] = otEl.GetString()!;
        if (orig.TryGetProperty("session", out var sesEl) && sesEl.ValueKind == JsonValueKind.String)
            placeBody["session"] = sesEl.GetString()!;
        if (orig.TryGetProperty("duration", out var durEl) && durEl.ValueKind == JsonValueKind.String)
            placeBody["duration"] = durEl.GetString()!;
        if (orig.TryGetProperty("orderStrategyType", out var ostEl) && ostEl.ValueKind == JsonValueKind.String)
            placeBody["orderStrategyType"] = ostEl.GetString()!;
        if (orig.TryGetProperty("complexOrderStrategyType", out var costEl) && costEl.ValueKind == JsonValueKind.String)
            placeBody["complexOrderStrategyType"] = costEl.GetString()!;

        var priceStr = replacement.NewLimitPrice?.ToString("F2")
                       ?? (orig.TryGetProperty("price", out var pEl) && pEl.ValueKind != JsonValueKind.Null
                           ? pEl.ToString() : null);
        if (priceStr != null) placeBody["price"] = priceStr;

        var stopStr = replacement.NewStopPrice?.ToString("F2")
                      ?? (orig.TryGetProperty("stopPrice", out var spEl) && spEl.ValueKind != JsonValueKind.Null
                          ? spEl.ToString() : null);
        if (stopStr != null) placeBody["stopPrice"] = stopStr;

        if (orig.TryGetProperty("orderLegCollection", out var legsEl) && legsEl.ValueKind == JsonValueKind.Array)
        {
            var newLegs = new List<Dictionary<string, object>>();
            foreach (var leg in legsEl.EnumerateArray())
            {
                var newLeg = new Dictionary<string, object>();
                if (leg.TryGetProperty("instruction", out var instEl) && instEl.ValueKind == JsonValueKind.String)
                    newLeg["instruction"] = instEl.GetString()!;
                var qty = replacement.NewQuantity
                          ?? (leg.TryGetProperty("quantity", out var qEl) ? TryGetQuantityValue(qEl) : 0);
                newLeg["quantity"] = qty;
                if (leg.TryGetProperty("instrument", out var instrEl) && instrEl.ValueKind == JsonValueKind.Object)
                {
                    var newInstr = new Dictionary<string, object>();
                    if (instrEl.TryGetProperty("symbol", out var symEl) && symEl.ValueKind == JsonValueKind.String)
                        newInstr["symbol"] = symEl.GetString()!;
                    if (instrEl.TryGetProperty("assetType", out var atEl) && atEl.ValueKind == JsonValueKind.String)
                        newInstr["assetType"] = atEl.GetString()!;
                    newLeg["instrument"] = newInstr;
                }
                newLegs.Add(newLeg);
            }
            placeBody["orderLegCollection"] = newLegs;
        }

        return placeBody;
    }

    private static int TryGetQuantityValue(JsonElement value)
    {
        if (value.ValueKind == JsonValueKind.Number)
        {
            if (value.TryGetInt32(out var intValue))
                return intValue;

            if (value.TryGetDecimal(out var decimalValue))
                return Convert.ToInt32(decimalValue);
        }

        if (value.ValueKind == JsonValueKind.String)
        {
            var text = value.GetString();
            if (decimal.TryParse(text, out var parsedDecimal))
                return Convert.ToInt32(parsedDecimal);
        }

        return 0;
    }

    private static string? ExtractOrderIdFromLocation(Uri? location)
    {
        if (location == null) return null;
        var path = location.IsAbsoluteUri ? location.AbsolutePath : location.OriginalString;
        var idx = path.LastIndexOf('/');
        if (idx < 0 || idx == path.Length - 1) return null;
        var id = path[(idx + 1)..];
        return string.IsNullOrWhiteSpace(id) ? null : id;
    }

    public async Task<OrderState?> GetOrderStatusAsync(string accountId, string brokerOrderId, CancellationToken ct = default)
    {
        var hash = GetHash(accountId);
        var resp = await GetAsync($"/accounts/{hash}/orders/{brokerOrderId}", ct);
        if (resp == null) return null;

        try
        {
            return ParseSingleOrder(accountId, resp.Value);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to parse order status");
            return null;
        }
    }

    public async Task<IReadOnlyList<OrderState>> SyncOrdersAsync(string accountId, CancellationToken ct = default)
    {
        var hash = GetHash(accountId);
        // Fetch last 7 days of orders
        var from = DateTime.UtcNow.AddDays(-7).ToString("yyyy-MM-ddTHH:mm:ssZ");
        var to = DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ssZ");
        var resp = await GetAsync($"/accounts/{hash}/orders?fromEnteredTime={from}&toEnteredTime={to}", ct);
        if (resp == null) return [];
        return ParseOrders(accountId, resp.Value);
    }

    private async Task<JsonElement?> GetAsync(string path, CancellationToken ct)
    {
        var token = await _auth.GetAccessTokenAsync(ct);
        if (string.IsNullOrEmpty(token)) return null;

        using var req = new HttpRequestMessage(HttpMethod.Get, $"{_config.TraderApiBase}{path}");
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);

        try
        {
            var resp = await _http.SendAsync(req, ct);
            if (!resp.IsSuccessStatusCode)
            {
                _logger.LogError("Schwab GET {Path} failed: {Status}", path, resp.StatusCode);
                return null;
            }
            var body = await resp.Content.ReadAsStringAsync(ct);
            return JsonDocument.Parse(body).RootElement.Clone();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Schwab GET {Path} exception", path);
            return null;
        }
    }

    private string GetHash(string accountId)
    {
        if (_accountHashes.TryGetValue(accountId, out var hash)) return hash;
        _logger.LogWarning("No hash for account {AccountId}, using accountId directly", accountId);
        return accountId;
    }

    private static AccountSummary ParseAccountSummary(string accountId, JsonElement root)
    {
        var acct = root.GetProperty("securitiesAccount");
        var summary = new AccountSummary
        {
            AccountId = accountId,
            AccountName = accountId,
        };

        if (acct.TryGetProperty("currentBalances", out var bal))
        {
            if (bal.TryGetProperty("buyingPower", out var bp)) summary.BuyingPower = bp.GetDecimal();
            if (bal.TryGetProperty("liquidationValue", out var lv)) summary.NetLiquidation = lv.GetDecimal();
            if (bal.TryGetProperty("dayTradingBuyingPower", out var dtbp)) summary.DayTradingBuyingPower = dtbp.GetDecimal();
        }

        if (acct.TryGetProperty("positions", out var positions))
        {
            foreach (var pos in positions.EnumerateArray())
            {
                var symbol = pos.GetProperty("instrument").GetProperty("symbol").GetString() ?? "";
                var qty = pos.TryGetProperty("longQuantity", out var lq) ? lq.GetDecimal() :
                          pos.TryGetProperty("shortQuantity", out var sq) ? -sq.GetDecimal() : 0;
                var avgPrice = pos.TryGetProperty("averagePrice", out var ap) ? ap.GetDecimal() : 0;
                var pnl = pos.TryGetProperty("currentDayProfitLoss", out var pl) ? pl.GetDecimal() : (decimal?)null;

                summary.Positions[symbol] = new Position
                {
                    AccountId = accountId,
                    Symbol = symbol,
                    Quantity = (int)qty,
                    AverageCost = avgPrice,
                    UnrealizedPnL = pnl
                };
            }
        }

        return summary;
    }

    private static IReadOnlyList<OrderState> ParseOrders(string accountId, JsonElement root)
    {
        // Schwab wraps TRIGGER / OCO / bracket strategies as a parent object
        // whose own fields are empty (quantity=0, no orderLegCollection) — the
        // real orders are inside `childOrderStrategies`. Recursively flatten so
        // we surface every real leaf order the account has.
        var orders = new List<OrderState>();
        foreach (var item in root.EnumerateArray())
            FlattenOrders(accountId, item, orders);
        return orders;
    }

    private static void FlattenOrders(string accountId, JsonElement item, List<OrderState> orders)
    {
        var parsed = ParseSingleOrder(accountId, item);
        // Only surface entries that look like a real leaf order — has a symbol,
        // has legs, and has a non-zero quantity. This drops Schwab's strategy
        // wrapper objects which would otherwise show up as blank rows.
        if (!string.IsNullOrEmpty(parsed.Symbol) && parsed.QuantityOrdered > 0)
            orders.Add(parsed);

        if (item.TryGetProperty("childOrderStrategies", out var kids) &&
            kids.ValueKind == JsonValueKind.Array)
        {
            foreach (var kid in kids.EnumerateArray())
                FlattenOrders(accountId, kid, orders);
        }
    }

    private static OrderState ParseSingleOrder(string accountId, JsonElement item)
    {
        var brokerId = item.TryGetProperty("orderId", out var oid) && oid.ValueKind == JsonValueKind.Number
                       ? oid.GetInt64().ToString() : "?";
        var status = item.TryGetProperty("status", out var st) ? MapStatus(st.GetString()) : OrderStatus.Unknown;
        var qty = item.TryGetProperty("quantity", out var q) && q.ValueKind == JsonValueKind.Number
                  ? (int)q.GetDecimal() : 0;
        var filled = item.TryGetProperty("filledQuantity", out var fq) && fq.ValueKind == JsonValueKind.Number
                     ? (int)fq.GetDecimal() : 0;
        var orderType = item.TryGetProperty("orderType", out var ot) ? ot.GetString() : null;
        var price = item.TryGetProperty("price", out var p) && p.ValueKind == JsonValueKind.Number
                    ? p.GetDecimal() : (decimal?)null;
        var stopPrice = item.TryGetProperty("stopPrice", out var sp) && sp.ValueKind == JsonValueKind.Number
                        ? sp.GetDecimal() : (decimal?)null;

        string symbol = "";
        OrderSide side = OrderSide.Buy;
        if (item.TryGetProperty("orderLegCollection", out var legs) &&
            legs.ValueKind == JsonValueKind.Array && legs.GetArrayLength() > 0)
        {
            var leg = legs[0];
            symbol = leg.TryGetProperty("instrument", out var inst) &&
                     inst.TryGetProperty("symbol", out var sym) ? sym.GetString() ?? "" : "";
            var instruction = leg.TryGetProperty("instruction", out var instr) ? instr.GetString() : "BUY";
            side = instruction is "BUY" or "BUY_TO_COVER" ? OrderSide.Buy : OrderSide.Sell;

            // Legs carry their own quantity when the outer strategy doesn't.
            if (qty == 0 && leg.TryGetProperty("quantity", out var lq) && lq.ValueKind == JsonValueKind.Number)
                qty = (int)lq.GetDecimal();
        }

        return new OrderState
        {
            ClientOrderId = brokerId,
            BrokerOrderId = brokerId,
            AccountId     = accountId,
            Symbol        = symbol,
            Side          = side,
            QuantityOrdered = qty,
            QuantityFilled = filled,
            OrderType     = MapOrderType(orderType),
            LimitPrice    = price,
            StopPrice     = stopPrice,
            Status        = status,
            Source        = OrderSource.System
        };
    }

    // Schwab has ~20 order statuses. Anything that means "live at broker, still
    // open" must map to Working / Accepted / PartiallyFilled so IsWorking=true
    // and the order shows on the DOM ladder. Unknown drops the order silently.
    private static OrderStatus MapStatus(string? s) => (s ?? "").ToUpperInvariant() switch
    {
        // Truly active in the book
        "WORKING"                 => OrderStatus.Working,
        "NEW"                     => OrderStatus.Working,

        // Accepted at broker; not yet routed or waiting on a condition
        "ACCEPTED"                => OrderStatus.Accepted,
        "PENDING_ACKNOWLEDGEMENT" => OrderStatus.Accepted,
        "AWAITING_PARENT_ORDER"   => OrderStatus.Accepted,
        "AWAITING_CONDITION"      => OrderStatus.Accepted,
        "AWAITING_STOP_CONDITION" => OrderStatus.Accepted,
        "AWAITING_MANUAL_REVIEW"  => OrderStatus.Accepted,
        "AWAITING_UR_OUT"         => OrderStatus.Accepted,
        "AWAITING_RELEASE_TIME"   => OrderStatus.Accepted,

        // Transient submission state
        "PENDING_ACTIVATION"      => OrderStatus.Submitted,
        "QUEUED"                  => OrderStatus.Submitted,

        // Fills
        "FILLED"                  => OrderStatus.Filled,
        "PARTIALLY_FILLED"        => OrderStatus.PartiallyFilled,

        // Cancel / replace lifecycle
        "CANCEL_REQUESTED"        => OrderStatus.CancelPending,
        "PENDING_CANCEL"          => OrderStatus.CancelPending,
        "PENDING_RECALL"          => OrderStatus.CancelPending,
        "REPLACE_REQUESTED"       => OrderStatus.ReplacePending,
        "PENDING_REPLACE"         => OrderStatus.ReplacePending,
        "REPLACED"                => OrderStatus.Replaced,

        // Terminal
        "CANCELED"                => OrderStatus.Cancelled, // Schwab spells with 1 L
        "CANCELLED"               => OrderStatus.Cancelled, // safety alias
        "REJECTED"                => OrderStatus.BrokerRejected,
        "EXPIRED"                 => OrderStatus.Cancelled,

        _                         => OrderStatus.Unknown
    };

    private static OrderType MapOrderType(string? s) => s switch
    {
        "LIMIT"      => OrderType.Limit,
        "STOP"       => OrderType.StopMarket,
        "STOP_LIMIT" => OrderType.StopLimit,
        _            => OrderType.Market
    };

    public ValueTask DisposeAsync()
    {
        _http.Dispose();
        _orderSubject.Dispose();
        _connectionSubject.Dispose();
        return ValueTask.CompletedTask;
    }
}
