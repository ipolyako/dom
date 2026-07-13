using System.Diagnostics;
using System.Globalization;
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
    private readonly List<AccountInfo> _accountCache = [];
    private DateTime _throttleUntilUtc = DateTime.MinValue;
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
        if (_accountCache.Count > 0)
            return _accountCache.ToList();

        var resp = await GetAsync("/accounts/accountNumbers", ct);
        if (resp == null) return _accountCache.ToList();

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
            _accountCache.Clear();
            _accountCache.AddRange(list);
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
        var totalSw = Stopwatch.StartNew();
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
            var httpSw = Stopwatch.StartNew();
            var resp = await _http.SendAsync(req, ct);
            _logger.LogInformation(
                "[LATENCY] Schwab POST order http account={Account} symbol={Symbol} status={Status} httpMs={HttpMs} totalMs={TotalMs}",
                request.AccountId, request.Symbol, (int)resp.StatusCode, httpSw.ElapsedMilliseconds, totalSw.ElapsedMilliseconds);
            if (resp.StatusCode == System.Net.HttpStatusCode.Created)
            {
                // Order ID is in the Location header
                var location = resp.Headers.Location?.ToString() ?? "";
                var orderId = location.Split('/').LastOrDefault() ?? "";
                _logger.LogInformation("Schwab order placed: {OrderId}", orderId);
                return OrderResult.Ok(orderId, request.ClientOrderId);
            }

            var body = await resp.Content.ReadAsStringAsync(ct);
            var reason = ExtractFailureMessage(body);
            _logger.LogError("Schwab PlaceOrder failed: {Status} {Reason}", resp.StatusCode, reason);
            return OrderResult.Fail($"Schwab rejected ({(int)resp.StatusCode}): {reason}", (int)resp.StatusCode);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Schwab PlaceOrder exception");
            return OrderResult.Fail(ex.Message);
        }
    }

    public async Task<OrderResult> CancelOrderAsync(string accountId, string brokerOrderId, CancellationToken ct = default)
    {
        var totalSw = Stopwatch.StartNew();
        var token = await _auth.GetAccessTokenAsync(ct);
        if (string.IsNullOrEmpty(token)) return OrderResult.Fail("Not authenticated");

        var hash = GetHash(accountId);
        using var req = new HttpRequestMessage(HttpMethod.Delete,
            $"{_config.TraderApiBase}/accounts/{hash}/orders/{brokerOrderId}");
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);

        try
        {
            var httpSw = Stopwatch.StartNew();
            var resp = await _http.SendAsync(req, ct);
            _logger.LogInformation(
                "[LATENCY] Schwab DELETE order http account={Account} order={OrderId} status={Status} httpMs={HttpMs} totalMs={TotalMs}",
                accountId, brokerOrderId, (int)resp.StatusCode, httpSw.ElapsedMilliseconds, totalSw.ElapsedMilliseconds);
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
            var reason = ExtractFailureMessage(body);
            return OrderResult.Fail($"Schwab cancel rejected ({(int)resp.StatusCode}): {reason}", (int)resp.StatusCode);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Schwab CancelOrder exception");
            return OrderResult.Fail(ex.Message);
        }
    }

    public async Task<OrderResult> ReplaceOrderAsync(string accountId, OrderReplace replacement, CancellationToken ct = default)
    {
        var totalSw = Stopwatch.StartNew();
        var token = await _auth.GetAccessTokenAsync(ct);
        if (string.IsNullOrEmpty(token)) return OrderResult.Fail("Not authenticated");

        // Schwab requires a full replacement payload for these equity orders.
        // A price-only PUT was consistently rejected with HTTP 400 and added a
        // full network round trip before this same full PUT. Fetch the current
        // broker payload first so external TOS changes are still reconciled,
        // then perform one atomic replacement request.
        var (fullPutResult, fullPutStatus) = await TryFullPutReplaceAsync(accountId, replacement, token, ct);
        _logger.LogInformation(
            "[LATENCY] Schwab replace full-put account={Account} order={OrderId} success={Success} status={Status} elapsedMs={ElapsedMs}",
            accountId, replacement.BrokerOrderId, fullPutResult.Success, fullPutStatus, totalSw.ElapsedMilliseconds);
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
            "Schwab full PUT replace failed (HTTP {FullStatus}); falling back to cancel+place",
            fullPutStatus);
        var result = await CancelAndPlaceReplacementAsync(accountId, replacement, token, ct);
        _logger.LogInformation(
            "[LATENCY] Schwab replace completed-via-fallback account={Account} order={OrderId} success={Success} newOrder={NewOrderId} totalMs={TotalMs}",
            accountId, replacement.BrokerOrderId, result.Success, result.BrokerOrderId, totalSw.ElapsedMilliseconds);
        return result;
    }

    private async Task<(OrderResult result, int status)> TryFullPutReplaceAsync(
        string accountId, OrderReplace replacement, string token, CancellationToken ct)
    {
        var hash = GetHash(accountId);
        var getSw = Stopwatch.StartNew();
        var (orig, error, status) = await GetOriginalOrderAsync(hash, replacement.BrokerOrderId, token, ct);
        _logger.LogInformation(
            "[LATENCY] Schwab full-put GET original account={Account} order={OrderId} status={Status} getMs={GetMs}",
            accountId, replacement.BrokerOrderId, status, getSw.ElapsedMilliseconds);
        if (orig == null)
            return (OrderResult.Fail($"Full PUT replace: GET failed {status} — {error}", status), status);

        var placeBody = BuildReplacementPayload(orig.Value, replacement);
        if (HasInvalidOcoPayload(placeBody))
            return (OrderResult.Fail("Full PUT replace: OCO changed externally or has fewer than 2 active children; refresh required", 409), 409);
        var json = JsonSerializer.Serialize(placeBody);

        using var req = new HttpRequestMessage(HttpMethod.Put,
            $"{_config.TraderApiBase}/accounts/{hash}/orders/{replacement.BrokerOrderId}");
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        req.Content = new StringContent(json, Encoding.UTF8, "application/json");

        try
        {
            var httpSw = Stopwatch.StartNew();
            var resp = await _http.SendAsync(req, ct);
            _logger.LogInformation(
                "[LATENCY] Schwab PUT full http account={Account} order={OrderId} status={Status} httpMs={HttpMs}",
                accountId, replacement.BrokerOrderId, (int)resp.StatusCode, httpSw.ElapsedMilliseconds);
            if (resp.IsSuccessStatusCode)
            {
                var newId = ExtractOrderIdFromLocation(resp.Headers.Location) ?? replacement.BrokerOrderId;
                return (OrderResult.Ok(newId), (int)resp.StatusCode);
            }

            var responseBody = await resp.Content.ReadAsStringAsync(ct);
            var reason = ExtractFailureMessage(responseBody);
            return (OrderResult.Fail($"Schwab replace rejected ({(int)resp.StatusCode}): {reason}", (int)resp.StatusCode), (int)resp.StatusCode);
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
        var totalSw = Stopwatch.StartNew();
        var hash = GetHash(accountId);

        // 1. GET original.
        var getSw = Stopwatch.StartNew();
        var (orig, error, status) = await GetOriginalOrderAsync(hash, replacement.BrokerOrderId, token, ct);
        _logger.LogInformation(
            "[LATENCY] Schwab fallback GET original account={Account} order={OrderId} status={Status} getMs={GetMs}",
            accountId, replacement.BrokerOrderId, status, getSw.ElapsedMilliseconds);
        if (orig == null)
            return OrderResult.Fail($"Cancel+place fallback: GET failed {status} — {error}", status);

        // 2. Build new order payload from original + replacement deltas.
        var placeBody = BuildReplacementPayload(orig.Value, replacement);
        if (HasInvalidOcoPayload(placeBody))
            return OrderResult.Fail("Cancel+place fallback: OCO changed externally or has fewer than 2 active children; refresh required", 409);

        // 3. DELETE old. Treat 409/422 (already terminal) as "out of the way".
        using var delReq = new HttpRequestMessage(HttpMethod.Delete,
            $"{_config.TraderApiBase}/accounts/{hash}/orders/{replacement.BrokerOrderId}");
        delReq.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        var deleteSw = Stopwatch.StartNew();
        var delResp = await _http.SendAsync(delReq, ct);
        var delCode = (int)delResp.StatusCode;
        _logger.LogInformation(
            "[LATENCY] Schwab fallback DELETE account={Account} order={OrderId} status={Status} deleteMs={DeleteMs}",
            accountId, replacement.BrokerOrderId, delCode, deleteSw.ElapsedMilliseconds);
        if (!delResp.IsSuccessStatusCode && delCode != 409 && delCode != 422)
        {
            var b = await delResp.Content.ReadAsStringAsync(ct);
            var reason = ExtractFailureMessage(b);
            return OrderResult.Fail($"Cancel+place fallback: DELETE failed {delCode} — {reason}");
        }
        await Task.Delay(150, ct);

        // 4. POST new.
        var placeJson = JsonSerializer.Serialize(placeBody);
        using var postReq = new HttpRequestMessage(HttpMethod.Post,
            $"{_config.TraderApiBase}/accounts/{hash}/orders");
        postReq.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        postReq.Content = new StringContent(placeJson, Encoding.UTF8, "application/json");
        var postSw = Stopwatch.StartNew();
        var postResp = await _http.SendAsync(postReq, ct);
        var postCode = (int)postResp.StatusCode;
        _logger.LogInformation(
            "[LATENCY] Schwab fallback POST replacement account={Account} oldOrder={OrderId} status={Status} postMs={PostMs} totalMs={TotalMs}",
            accountId, replacement.BrokerOrderId, postCode, postSw.ElapsedMilliseconds, totalSw.ElapsedMilliseconds);
        if (postResp.StatusCode == System.Net.HttpStatusCode.Created)
        {
            var newId = ExtractOrderIdFromLocation(postResp.Headers.Location) ?? "";
            _logger.LogInformation("Schwab cancel+place fallback succeeded: new order {NewId}", newId);
            return OrderResult.Ok(newId);
        }
        var postBody = await postResp.Content.ReadAsStringAsync(ct);
        var postReason = ExtractFailureMessage(postBody);
        return OrderResult.Fail($"Cancel+place fallback: POST failed {postCode} — {postReason}", postCode);
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

        if (orig.TryGetProperty("childOrderStrategies", out var childrenEl) && childrenEl.ValueKind == JsonValueKind.Array)
        {
            var children = new List<Dictionary<string, object>>();
            foreach (var child in childrenEl.EnumerateArray())
                children.Add(BuildReplacementPayload(child, replacement));
            placeBody["childOrderStrategies"] = children;
            return placeBody;
        }

        var priceStr = IsLimitLike(orig) && replacement.NewLimitPrice.HasValue
                       ? replacement.NewLimitPrice.Value.ToString("F2")
                       : (orig.TryGetProperty("price", out var pEl) && pEl.ValueKind != JsonValueKind.Null
                           ? pEl.ToString() : null);
        if (priceStr != null) placeBody["price"] = priceStr;

        var stopStr = IsStopLike(orig) && replacement.NewStopPrice.HasValue
                      ? replacement.NewStopPrice.Value.ToString("F2")
                      : (orig.TryGetProperty("stopPrice", out var spEl) && spEl.ValueKind != JsonValueKind.Null
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

    private static bool HasInvalidOcoPayload(Dictionary<string, object> payload)
    {
        if (payload.TryGetValue("childOrderStrategies", out var childrenObj) &&
            childrenObj is List<Dictionary<string, object>> children)
        {
            if (payload.TryGetValue("orderStrategyType", out var strategyObj) &&
                strategyObj is string strategy &&
                string.Equals(strategy, "OCO", StringComparison.OrdinalIgnoreCase) &&
                children.Count != 2)
                return true;

            foreach (var child in children)
            {
                if (HasInvalidOcoPayload(child))
                    return true;
            }
        }

        return false;
    }

    private static bool IsLimitLike(JsonElement order) =>
        order.TryGetProperty("orderType", out var otEl)
        && otEl.ValueKind == JsonValueKind.String
        && otEl.GetString()?.Contains("LIMIT", StringComparison.OrdinalIgnoreCase) == true;

    private static bool IsStopLike(JsonElement order) =>
        order.TryGetProperty("orderType", out var otEl)
        && otEl.ValueKind == JsonValueKind.String
        && otEl.GetString()?.Contains("STOP", StringComparison.OrdinalIgnoreCase) == true;

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
            if (IsOcoStrategy(resp.Value))
                return ParseOcoOrder(accountId, resp.Value);

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
        var sw = Stopwatch.StartNew();
        var hash = GetHash(accountId);
        // Fetch last 7 days of orders
        var from = DateTime.UtcNow.AddDays(-7).ToString("yyyy-MM-ddTHH:mm:ssZ");
        var to = DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ssZ");
        var resp = await GetAsync($"/accounts/{hash}/orders?fromEnteredTime={from}&toEnteredTime={to}", ct);
        if (resp == null) return [];
        var orders = ParseOrders(accountId, resp.Value);
        _logger.LogInformation(
            "[LATENCY] Schwab SyncOrders account={Account} count={Count} totalMs={TotalMs}",
            accountId, orders.Count, sw.ElapsedMilliseconds);
        return orders;
    }

    private async Task<JsonElement?> GetAsync(string path, CancellationToken ct)
    {
        if (DateTime.UtcNow < _throttleUntilUtc)
        {
            _logger.LogDebug("Schwab GET {Path} skipped during rate-limit cooldown until {Until}", path, _throttleUntilUtc);
            return null;
        }

        var token = await _auth.GetAccessTokenAsync(ct);
        if (string.IsNullOrEmpty(token)) return null;

        using var req = new HttpRequestMessage(HttpMethod.Get, $"{_config.TraderApiBase}{path}");
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);

        try
        {
            var sw = Stopwatch.StartNew();
            var resp = await _http.SendAsync(req, ct);
            _logger.LogInformation(
                "[LATENCY] Schwab GET path={Path} status={Status} httpMs={HttpMs}",
                path, (int)resp.StatusCode, sw.ElapsedMilliseconds);
            if (!resp.IsSuccessStatusCode)
            {
                if ((int)resp.StatusCode == 429)
                {
                    _throttleUntilUtc = DateTime.UtcNow.AddSeconds(45);
                    _logger.LogWarning("Schwab rate limit hit on GET {Path}; pausing broker polling until {Until}", path, _throttleUntilUtc);
                    return null;
                }

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
                var longQty = pos.TryGetProperty("longQuantity", out var lq) ? lq.GetDecimal() : 0m;
                var shortQty = pos.TryGetProperty("shortQuantity", out var sq) ? sq.GetDecimal() : 0m;
                var qty = longQty - shortQty;
                var avgPrice = pos.TryGetProperty("averagePrice", out var ap) ? ap.GetDecimal() : 0;
                var dayPnl = TryGetDecimal(pos, "currentDayProfitLoss");
                var openPnl = qty >= 0
                    ? TryGetDecimal(pos, "longOpenProfitLoss")
                    : TryGetDecimal(pos, "shortOpenProfitLoss");

                summary.Positions[symbol] = new Position
                {
                    AccountId = accountId,
                    Symbol = symbol,
                    Quantity = (int)qty,
                    AverageCost = avgPrice,
                    UnrealizedPnL = openPnl,
                    DayPnL = dayPnl
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
        if (IsOcoStrategy(item))
        {
            var oco = ParseOcoOrder(accountId, item);
            if (oco != null)
                orders.Add(oco);
            return;
        }

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

    private static bool IsOcoStrategy(JsonElement item) =>
        item.TryGetProperty("orderStrategyType", out var ost)
        && ost.ValueKind == JsonValueKind.String
        && string.Equals(ost.GetString(), "OCO", StringComparison.OrdinalIgnoreCase)
        && item.TryGetProperty("childOrderStrategies", out var kids)
        && kids.ValueKind == JsonValueKind.Array;

    private static OrderState? ParseOcoOrder(string accountId, JsonElement item)
    {
        if (!item.TryGetProperty("childOrderStrategies", out var kids) || kids.ValueKind != JsonValueKind.Array)
            return null;

        JsonElement? limitChild = null;
        JsonElement? stopChild = null;
        foreach (var kid in kids.EnumerateArray())
        {
            if (IsLimitLike(kid) && limitChild == null)
                limitChild = kid;
            else if (IsStopLike(kid) && stopChild == null)
                stopChild = kid;
        }

        if (limitChild == null && stopChild == null)
            return null;

        var primary = limitChild ?? stopChild!.Value;
        var parsed = ParseSingleOrder(accountId, primary);
        if (string.IsNullOrEmpty(parsed.Symbol) || parsed.QuantityOrdered <= 0)
            return null;

        var parentId = item.TryGetProperty("orderId", out var oid) && oid.ValueKind == JsonValueKind.Number
            ? oid.GetInt64().ToString()
            : parsed.BrokerOrderId;

        var status = MapOcoStatus(item, limitChild, stopChild);
        var limitId = limitChild.HasValue ? TryGetOrderId(limitChild.Value) : null;
        var stopId = stopChild.HasValue ? TryGetOrderId(stopChild.Value) : null;
        var limitPrice = limitChild.HasValue ? TryGetDecimal(limitChild.Value, "price") : null;
        var stopPrice = stopChild.HasValue ? TryGetDecimal(stopChild.Value, "stopPrice") : null;
        var messages = new List<string>();
        var parentMessage = TryGetOrderMessage(item);
        if (!string.IsNullOrWhiteSpace(parentMessage))
            messages.Add(parentMessage);
        if (limitChild.HasValue)
        {
            var limitMessage = TryGetOrderMessage(limitChild.Value);
            if (!string.IsNullOrWhiteSpace(limitMessage))
                messages.Add($"Limit: {limitMessage}");
        }
        if (stopChild.HasValue)
        {
            var stopMessage = TryGetOrderMessage(stopChild.Value);
            if (!string.IsNullOrWhiteSpace(stopMessage))
                messages.Add($"Stop: {stopMessage}");
        }
        var brokerMessage = messages.Count > 0 ? string.Join(" | ", messages) : null;

        return new OrderState
        {
            ClientOrderId = parentId ?? parsed.ClientOrderId,
            BrokerOrderId = parentId ?? parsed.BrokerOrderId,
            ParentOrderId = parentId,
            LimitLegOrderId = limitId,
            StopLegOrderId = stopId,
            IsOcoGroup = true,
            AccountId = accountId,
            Symbol = parsed.Symbol,
            Side = parsed.Side,
            QuantityOrdered = parsed.QuantityOrdered,
            QuantityFilled = parsed.QuantityFilled,
            OrderType = OrderType.Bracket,
            LimitPrice = limitPrice,
            StopPrice = stopPrice,
            AverageFillPrice = parsed.AverageFillPrice,
            Status = status,
            BrokerMessage = brokerMessage,
            Source = OrderSource.System,
            CreatedAtUtc = parsed.CreatedAtUtc,
            LastUpdatedUtc = parsed.LastUpdatedUtc
        };
    }

    private static OrderStatus MapOcoStatus(JsonElement parent, JsonElement? limitChild, JsonElement? stopChild)
    {
        var parentStatus = parent.TryGetProperty("status", out var pst) ? MapStatus(pst.GetString()) : OrderStatus.Unknown;
        var limitStatus = limitChild.HasValue && limitChild.Value.TryGetProperty("status", out var lst)
            ? MapStatus(lst.GetString())
            : OrderStatus.Unknown;
        var stopStatus = stopChild.HasValue && stopChild.Value.TryGetProperty("status", out var sst)
            ? MapStatus(sst.GetString())
            : OrderStatus.Unknown;

        if (IsWorkingStatus(limitStatus) || IsWorkingStatus(stopStatus))
            return OrderStatus.Working;
        if (limitStatus == OrderStatus.PartiallyFilled || stopStatus == OrderStatus.PartiallyFilled)
            return OrderStatus.PartiallyFilled;
        if (limitStatus == OrderStatus.Filled || stopStatus == OrderStatus.Filled)
            return OrderStatus.Filled;
        if (parentStatus != OrderStatus.Unknown)
            return parentStatus;
        if (limitStatus != OrderStatus.Unknown)
            return limitStatus;
        return stopStatus;
    }

    private static bool IsWorkingStatus(OrderStatus status) =>
        status is OrderStatus.Working or OrderStatus.Accepted or OrderStatus.Submitted or OrderStatus.ReplacePending or OrderStatus.CancelPending;

    private static string? TryGetOrderId(JsonElement item) =>
        item.TryGetProperty("orderId", out var oid) && oid.ValueKind == JsonValueKind.Number
            ? oid.GetInt64().ToString()
            : null;

    private static decimal? TryGetDecimal(JsonElement item, string property)
    {
        if (!item.TryGetProperty(property, out var value))
            return null;

        return TryGetDecimal(value);
    }

    private static decimal? TryGetDecimal(JsonElement value)
    {
        return value.ValueKind switch
        {
            JsonValueKind.Number => value.GetDecimal(),
            JsonValueKind.String => decimal.TryParse(
                value.GetString(),
                NumberStyles.Number,
                CultureInfo.InvariantCulture,
                out var parsed)
                ? parsed
                : null,
            _ => null
        };
    }

    private static OrderState ParseSingleOrder(string accountId, JsonElement item)
    {
        var brokerId = item.TryGetProperty("orderId", out var oid) && oid.ValueKind == JsonValueKind.Number
                       ? oid.GetInt64().ToString() : "?";
        var status = item.TryGetProperty("status", out var st) ? MapStatus(st.GetString()) : OrderStatus.Unknown;
        var qty = item.TryGetProperty("quantity", out var q) ? TryGetQuantityValue(q) : 0;
        var filled = item.TryGetProperty("filledQuantity", out var fq) ? TryGetQuantityValue(fq) : 0;
        var orderType = item.TryGetProperty("orderType", out var ot) ? ot.GetString() : null;
        var price = TryGetDecimal(item, "price");
        var stopPrice = TryGetDecimal(item, "stopPrice");
        var (execQty, execAvgPrice) = TryGetExecutionFill(item);
        if (execQty > 0)
            filled = execQty;

        var avgFillPrice = execAvgPrice
            ?? TryGetDecimal(item, "averagePrice")
            ?? TryGetDecimal(item, "price");
        var createdAt = TryGetDateTimeUtc(item, "enteredTime") ?? DateTime.UtcNow;
        var updatedAt = TryGetDateTimeUtc(item, "closeTime")
            ?? TryGetDateTimeUtc(item, "enteredTime")
            ?? DateTime.UtcNow;

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
            if (qty == 0 && leg.TryGetProperty("quantity", out var lq))
                qty = TryGetQuantityValue(lq);
        }

        var resolvedType = MapOrderType(orderType);
        if (resolvedType == OrderType.Market && stopPrice.HasValue)
            resolvedType = price.HasValue ? OrderType.StopLimit : OrderType.StopMarket;

        return new OrderState
        {
            ClientOrderId = brokerId,
            BrokerOrderId = brokerId,
            AccountId     = accountId,
            Symbol        = symbol,
            Side          = side,
            QuantityOrdered = qty,
            QuantityFilled = filled,
            OrderType     = resolvedType,
            LimitPrice    = price,
            StopPrice     = stopPrice,
            AverageFillPrice = filled > 0 ? avgFillPrice : null,
            Status        = status,
            BrokerMessage = TryGetOrderMessage(item),
            Source        = OrderSource.System,
            CreatedAtUtc  = updatedAt.Date == DateTime.UtcNow.Date ? updatedAt : createdAt,
            LastUpdatedUtc = updatedAt
        };
    }

    private static string? TryGetOrderMessage(JsonElement item)
    {
        var candidates = new[]
        {
            "statusDescription", "statusReason", "rejectReason", "rejectionReason", "reason", "message",
            "error", "error_description", "description", "title", "code", "errorCode"
        };
        foreach (var key in candidates)
        {
            if (!item.TryGetProperty(key, out var value))
                continue;

            switch (value.ValueKind)
            {
                case JsonValueKind.String:
                    var text = value.GetString();
                    if (!string.IsNullOrWhiteSpace(text))
                        return text;
                    break;
                case JsonValueKind.Number:
                    var num = value.GetRawText();
                    if (!string.IsNullOrWhiteSpace(num))
                        return num;
                    break;
                case JsonValueKind.Object when key == "error":
                    if (TryGetNestedText(value, out var nested))
                        return nested;
                    break;
            }
        }

        if (item.TryGetProperty("errors", out var errorsEl) &&
            errorsEl.ValueKind == JsonValueKind.Array)
        {
            var parts = new List<string>();
            foreach (var e in errorsEl.EnumerateArray())
            {
                var msg = ExtractFailureMessage(e.GetRawText());
                if (!string.IsNullOrWhiteSpace(msg))
                    parts.Add(msg);
            }
            if (parts.Count > 0) return string.Join("; ", parts);
        }

        return null;
    }

    private static bool TryGetNestedText(JsonElement errorObj, out string? text)
    {
        if (errorObj.ValueKind != JsonValueKind.Object)
        {
            text = null;
            return false;
        }

        foreach (var key in new[] { "message", "error", "description", "code", "title" })
        {
            if (!errorObj.TryGetProperty(key, out var nested))
                continue;

            if (nested.ValueKind == JsonValueKind.String)
            {
                var n = nested.GetString();
                if (!string.IsNullOrWhiteSpace(n))
                {
                    text = n;
                    return true;
                }
            }
        }

        text = null;
        return false;
    }

    private static string ExtractFailureMessage(string body)
    {
        if (string.IsNullOrWhiteSpace(body))
            return "Empty response body";

        try
        {
            using var doc = JsonDocument.Parse(body);
            return ExtractFailureMessage(doc.RootElement);
        }
        catch
        {
            return body;
        }
    }

    private static string ExtractFailureMessage(JsonElement root)
    {
        if (TryGetNestedMessage(root, out var message))
            return message!;

        if (root.ValueKind == JsonValueKind.Array)
        {
            var messages = new List<string>();
            foreach (var item in root.EnumerateArray())
            {
                if (TryGetNestedMessage(item, out var entry) && !string.IsNullOrWhiteSpace(entry))
                    messages.Add(entry);
            }
            if (messages.Count > 0)
                return string.Join("; ", messages);
        }

        if (root.ValueKind == JsonValueKind.Object &&
            root.TryGetProperty("errors", out var errorsEl) &&
            errorsEl.ValueKind == JsonValueKind.Array)
        {
            var messages = new List<string>();
            foreach (var err in errorsEl.EnumerateArray())
            {
                if (TryGetNestedMessage(err, out var entry))
                    messages.Add(entry!);
            }
            if (messages.Count > 0)
                return string.Join("; ", messages);
        }

        return root.GetRawText();
    }

    private static bool TryGetNestedMessage(JsonElement root, out string? message)
    {
        message = null;
        if (root.ValueKind != JsonValueKind.Object)
            return false;

        foreach (var key in new[]
                 {
                     "error", "message", "title", "description", "detail", "reason",
                     "statusDescription", "statusReason", "rejectReason", "rejectionReason",
                     "error_description", "errorCode"
                 })
        {
            if (!root.TryGetProperty(key, out var value))
                continue;

            if (value.ValueKind == JsonValueKind.String)
            {
                var text = value.GetString();
                if (!string.IsNullOrWhiteSpace(text))
                {
                    message = text;
                    return true;
                }
            }

            if (value.ValueKind == JsonValueKind.Number)
            {
                var text = value.GetRawText();
                if (!string.IsNullOrWhiteSpace(text))
                {
                    message = text;
                    return true;
                }
            }

            if (value.ValueKind == JsonValueKind.Object && key == "error")
            {
                if (TryGetNestedText(value, out var nested))
                {
                    message = nested;
                    return true;
                }
            }
        }

        return false;
    }

    private static (int Quantity, decimal? AveragePrice) TryGetExecutionFill(JsonElement item)
    {
        if (!item.TryGetProperty("orderActivityCollection", out var activities) ||
            activities.ValueKind != JsonValueKind.Array)
            return (0, null);

        decimal totalQty = 0m;
        decimal totalValue = 0m;
        foreach (var activity in activities.EnumerateArray())
        {
            if (!activity.TryGetProperty("executionLegs", out var legs) ||
                legs.ValueKind != JsonValueKind.Array)
                continue;

            foreach (var leg in legs.EnumerateArray())
            {
                var qty = TryGetDecimal(leg, "quantity") ?? 0m;
                var price = TryGetDecimal(leg, "price") ?? 0m;
                if (qty <= 0 || price <= 0) continue;

                totalQty += qty;
                totalValue += qty * price;
            }
        }

        return totalQty > 0
            ? ((int)totalQty, totalValue / totalQty)
            : (0, null);
    }

    private static DateTime? TryGetDateTimeUtc(JsonElement item, string property)
    {
        if (!item.TryGetProperty(property, out var value) || value.ValueKind != JsonValueKind.String)
            return null;

        return DateTime.TryParse(value.GetString(), out var dt)
            ? dt.ToUniversalTime()
            : null;
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

    private static OrderType MapOrderType(string? s)
    {
        if (string.IsNullOrWhiteSpace(s))
            return OrderType.Market;

        var value = s.Trim().ToUpperInvariant();
        return value switch
        {
            "LIMIT"        => OrderType.Limit,
            "MARKET"       => OrderType.Market,
            "MARKET_IF_TOUCHED" => OrderType.MarketableLimit,
            "STOP"         => OrderType.StopMarket,
            "STOP_LIMIT"   => OrderType.StopLimit,
            "MARKETABLE_LIMIT"  => OrderType.MarketableLimit,
            "BRACKET"      => OrderType.Bracket,
            "OCO"          => OrderType.OCO,
            "OSO"          => OrderType.OSO,
            _ when value.Contains("STOP_LIMIT") => OrderType.StopLimit,
            _ when value.Contains("STOP") => OrderType.StopMarket,
            _ when value.Contains("LIMIT") => OrderType.Limit,
            _ => OrderType.Market
        };
    }

    public ValueTask DisposeAsync()
    {
        _http.Dispose();
        _orderSubject.Dispose();
        _connectionSubject.Dispose();
        return ValueTask.CompletedTask;
    }
}
