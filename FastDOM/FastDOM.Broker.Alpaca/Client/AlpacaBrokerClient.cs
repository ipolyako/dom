using System.Net.Http.Headers;
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
    private bool _connected;

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

    public async Task ConnectAsync(CancellationToken ct = default)
    {
        try
        {
            var resp = await _http.GetAsync($"{_config.TraderApiBase}/account", ct);
            resp.EnsureSuccessStatusCode();
            _connected = true;
            _connectionSubject.OnNext(true);
            _logger.LogInformation("Alpaca connected ({Mode})", BrokerName);
        }
        catch (Exception ex)
        {
            _connected = false;
            _connectionSubject.OnNext(false);
            _logger.LogError(ex, "Alpaca connection failed");
        }
    }

    public Task DisconnectAsync(CancellationToken ct = default)
    {
        _connected = false;
        _connectionSubject.OnNext(false);
        return Task.CompletedTask;
    }

    public async Task<IReadOnlyList<AccountInfo>> GetAccountsAsync(CancellationToken ct = default)
    {
        try
        {
            var resp = await _http.GetAsync($"{_config.TraderApiBase}/account", ct);
            resp.EnsureSuccessStatusCode();
            var body = await resp.Content.ReadAsStringAsync(ct);
            using var doc = JsonDocument.Parse(body);
            var id = doc.RootElement.GetProperty("id").GetString() ?? "";
            return [new AccountInfo { AccountId = id, AccountHash = id, DisplayName = BrokerName, AccountType = "Alpaca" }];
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
            var resp = await _http.GetAsync($"{_config.TraderApiBase}/account", ct);
            resp.EnsureSuccessStatusCode();
            var body = await resp.Content.ReadAsStringAsync(ct);
            using var doc = JsonDocument.Parse(body);
            var root = doc.RootElement;

            return new AccountSummary
            {
                AccountId          = accountId,
                AccountName        = BrokerName,
                NetLiquidation     = ParseDecimal(root, "equity"),
                BuyingPower        = ParseDecimal(root, "buying_power"),
                DailyUnrealizedPnL = ParseDecimal(root, "unrealized_intraday_pl"),
            };
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "GetAccountSummaryAsync failed");
            return new AccountSummary { AccountId = accountId, AccountName = BrokerName };
        }
    }

    public async Task<IReadOnlyList<OrderState>> GetOpenOrdersAsync(string accountId, CancellationToken ct = default)
    {
        try
        {
            var resp = await _http.GetAsync($"{_config.TraderApiBase}/orders?status=open", ct);
            resp.EnsureSuccessStatusCode();
            var body = await resp.Content.ReadAsStringAsync(ct);
            using var doc = JsonDocument.Parse(body);
            return doc.RootElement.EnumerateArray()
                      .Select(e => MapOrder(e, accountId))
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
                time_in_force = "day",
                limit_price   = request.LimitPrice > 0 ? ((decimal)request.LimitPrice).ToString("F2") : null,
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
                return OrderResult.Fail($"Alpaca rejected: {resp.StatusCode}");
            }

            using var doc = JsonDocument.Parse(body);
            var orderId = doc.RootElement.GetProperty("id").GetString() ?? "";
            _logger.LogInformation("Alpaca order placed: {Id}", orderId);
            return OrderResult.Ok(orderId, request.ClientOrderId);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "PlaceOrderAsync failed");
            return OrderResult.Fail(ex.Message);
        }
    }

    public async Task<OrderResult> CancelOrderAsync(string accountId, string brokerOrderId, CancellationToken ct = default)
    {
        try
        {
            var resp = await _http.DeleteAsync($"{_config.TraderApiBase}/orders/{brokerOrderId}", ct);
            resp.EnsureSuccessStatusCode();
            return OrderResult.Ok(brokerOrderId, "");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "CancelOrderAsync failed");
            return OrderResult.Fail(ex.Message);
        }
    }

    public async Task<OrderResult> ReplaceOrderAsync(string accountId, OrderReplace replacement, CancellationToken ct = default)
    {
        try
        {
            var payload = new { qty = replacement.NewQuantity?.ToString(), limit_price = replacement.NewLimitPrice?.ToString("F2") };
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
                return OrderResult.Fail($"Replace failed: {resp.StatusCode}");
            using var doc = JsonDocument.Parse(body);
            var newId = doc.RootElement.GetProperty("id").GetString() ?? "";
            return OrderResult.Ok(newId, replacement.OriginalClientOrderId);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "ReplaceOrderAsync failed");
            return OrderResult.Fail(ex.Message);
        }
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
        => await GetOpenOrdersAsync(accountId, ct);

    private static OrderState MapOrder(JsonElement e, string accountId)
    {
        var statusStr = e.TryGetProperty("status", out var s) ? s.GetString() : "";
        var brokerQty = e.TryGetProperty("qty", out var qtyEl)
            ? (int)Math.Round(decimal.Parse(qtyEl.GetString() ?? "0")) : 0;
        var state = new OrderState
        {
            ClientOrderId  = e.TryGetProperty("client_order_id", out var cid) ? cid.GetString() ?? "" : "",
            BrokerOrderId  = e.TryGetProperty("id", out var id) ? id.GetString() : null,
            AccountId      = accountId,
            Symbol         = e.TryGetProperty("symbol", out var sym) ? sym.GetString() ?? "" : "",
            Side           = e.TryGetProperty("side", out var side) && side.GetString() == "sell"
                                 ? OrderSide.Sell : OrderSide.Buy,
            QuantityOrdered = brokerQty,
            OrderType      = OrderType.Market,
            Source         = OrderSource.System,
            Status         = MapStatus(statusStr),
        };
        if (e.TryGetProperty("filled_qty", out var fq) && fq.ValueKind != JsonValueKind.Null)
            state.QuantityFilled = (int)Math.Round(decimal.Parse(fq.GetString() ?? "0"));
        if (e.TryGetProperty("filled_avg_price", out var fp) && fp.ValueKind != JsonValueKind.Null
            && decimal.TryParse(fp.GetString(), out var fpv))
            state.AverageFillPrice = fpv;
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
        _                                    => OrderStatus.Unknown,
    };

    private static string MapOrderType(OrderType t) => t switch
    {
        OrderType.Market          => "market",
        OrderType.Limit           => "limit",
        OrderType.StopMarket      => "stop",
        OrderType.StopLimit       => "stop_limit",
        OrderType.MarketableLimit => "limit",
        _                         => "market",
    };

    private static decimal ParseDecimal(JsonElement root, string key)
    {
        if (!root.TryGetProperty(key, out var prop)) return 0;
        if (prop.ValueKind == JsonValueKind.Number) return prop.GetDecimal();
        if (prop.ValueKind == JsonValueKind.String && decimal.TryParse(prop.GetString(), out var v)) return v;
        return 0;
    }

    public async ValueTask DisposeAsync()
    {
        if (_connected)
            await DisconnectAsync();
        _orderSubject.Dispose();
        _connectionSubject.Dispose();
        _http.Dispose();
    }
}
