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

        var hash = GetHash(accountId);

        var body = new Dictionary<string, object>();
        if (replacement.NewLimitPrice.HasValue)
            body["price"] = replacement.NewLimitPrice.Value.ToString("F2");
        if (replacement.NewStopPrice.HasValue)
            body["stopPrice"] = replacement.NewStopPrice.Value.ToString("F2");
        if (replacement.NewQuantity.HasValue)
        {
            // Replace requires the full order body, so we need quantity in a leg
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
                // Schwab returns 201 Created with Location: .../orders/{NEW_ID}.
                // The old order becomes REPLACED; the new order gets a fresh id.
                // Return that so OrderService can re-key its state under the new
                // id and ignore the stale REPLACED stream update for the old one.
                var newId = ExtractOrderIdFromLocation(resp.Headers.Location)
                            ?? replacement.BrokerOrderId;
                return OrderResult.Ok(newId);
            }

            var responseBody = await resp.Content.ReadAsStringAsync(ct);
            return OrderResult.Fail(responseBody, (int)resp.StatusCode);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Schwab ReplaceOrder exception");
            return OrderResult.Fail(ex.Message);
        }
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
        var orders = new List<OrderState>();
        foreach (var item in root.EnumerateArray())
            orders.Add(ParseSingleOrder(accountId, item));
        return orders;
    }

    private static OrderState ParseSingleOrder(string accountId, JsonElement item)
    {
        var brokerId = item.TryGetProperty("orderId", out var oid) ? oid.GetInt64().ToString() : "?";
        var status = item.TryGetProperty("status", out var st) ? MapStatus(st.GetString()) : OrderStatus.Unknown;
        var qty = item.TryGetProperty("quantity", out var q) ? (int)q.GetDecimal() : 0;
        var filled = item.TryGetProperty("filledQuantity", out var fq) ? (int)fq.GetDecimal() : 0;
        var orderType = item.TryGetProperty("orderType", out var ot) ? ot.GetString() : "MARKET";
        var price = item.TryGetProperty("price", out var p) ? p.GetDecimal() : (decimal?)null;
        var stopPrice = item.TryGetProperty("stopPrice", out var sp) ? sp.GetDecimal() : (decimal?)null;

        string symbol = "";
        OrderSide side = OrderSide.Buy;
        if (item.TryGetProperty("orderLegCollection", out var legs) && legs.GetArrayLength() > 0)
        {
            var leg = legs[0];
            symbol = leg.TryGetProperty("instrument", out var inst) &&
                     inst.TryGetProperty("symbol", out var sym) ? sym.GetString() ?? "" : "";
            var instruction = leg.TryGetProperty("instruction", out var instr) ? instr.GetString() : "BUY";
            side = instruction is "BUY" or "BUY_TO_COVER" ? OrderSide.Buy : OrderSide.Sell;
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

    private static OrderStatus MapStatus(string? s) => s switch
    {
        "WORKING"            => OrderStatus.Working,
        "ACCEPTED"           => OrderStatus.Accepted,
        "FILLED"             => OrderStatus.Filled,
        "PARTIALLY_FILLED"   => OrderStatus.PartiallyFilled,
        "CANCELLED"          => OrderStatus.Cancelled,
        "REJECTED"           => OrderStatus.BrokerRejected,
        "CANCEL_REQUESTED"   => OrderStatus.CancelPending,
        "REPLACE_REQUESTED"  => OrderStatus.ReplacePending,
        "REPLACED"           => OrderStatus.Replaced,
        "PENDING_ACTIVATION" => OrderStatus.Submitted,
        "QUEUED"             => OrderStatus.Submitted,
        _                    => OrderStatus.Unknown
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
