using System.Net.WebSockets;
using System.Reactive.Linq;
using System.Reactive.Subjects;
using System.Text;
using System.Text.Json;
using FastDOM.Infrastructure.Config;
using FastDOM.MarketData.Interfaces;
using FastDOM.MarketData.Models;
using Microsoft.Extensions.Logging;

namespace FastDOM.Broker.Alpaca.Client;

public class AlpacaMarketDataClient : IMarketDataClient
{
    private readonly ILogger<AlpacaMarketDataClient> _logger;
    private readonly AlpacaConfig _config;
    private readonly HttpClient _http;
    private readonly Subject<Quote> _quoteSubject = new();
    private readonly Subject<MarketDepth> _depthSubject = new();
    private readonly Subject<Trade> _tradeSubject = new();
    private readonly Subject<AccountActivity> _accountActivitySubject = new();
    private readonly Subject<bool> _connectionSubject = new();
    private ClientWebSocket? _ws;
    private CancellationTokenSource? _wsCts;
    private bool _connected;
    private bool _disposed;
    // Last known quote per symbol — updated by both quote ticks and trade ticks
    private readonly Dictionary<string, Quote> _latestQuote = new(StringComparer.OrdinalIgnoreCase);

    public bool IsConnected => _connected;
    public bool SupportsLevelTwo => false;
    public IObservable<Quote> QuoteStream => _quoteSubject.AsObservable();
    public IObservable<MarketDepth> DepthStream => _depthSubject.AsObservable();
    public IObservable<Trade> TradeStream => _tradeSubject.AsObservable();
    public IObservable<AccountActivity> AccountActivityStream => _accountActivitySubject.AsObservable();
    public IObservable<bool> ConnectionStateStream => _connectionSubject.AsObservable();

    public AlpacaMarketDataClient(ILogger<AlpacaMarketDataClient> logger, AlpacaConfig config)
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
            _wsCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            _ws = new ClientWebSocket();
            await _ws.ConnectAsync(new Uri(_config.DataStreamBase), _wsCts.Token);

            // Authenticate
            var auth = JsonSerializer.Serialize(new
            {
                action = "auth",
                key    = _config.ApiKey,
                secret = _config.ApiSecret,
            });
            await SendWsAsync(auth, _wsCts.Token);

            _ = Task.Run(() => ReceiveLoopAsync(_wsCts.Token), _wsCts.Token);

            _connected = true;
            _connectionSubject.OnNext(true);
            _logger.LogInformation("Alpaca market data WebSocket connected");
        }
        catch (Exception ex)
        {
            _connected = false;
            _connectionSubject.OnNext(false);
            _logger.LogError(ex, "Alpaca market data connect failed");
        }
    }

    public async Task DisconnectAsync(CancellationToken ct = default)
    {
        _wsCts?.Cancel();
        if (_ws?.State == WebSocketState.Open)
        {
            try { await _ws.CloseAsync(WebSocketCloseStatus.NormalClosure, "disconnect", CancellationToken.None); }
            catch { /* best-effort */ }
        }
        _ws?.Dispose();
        _ws = null;
        _connected = false;
        if (!_disposed) _connectionSubject.OnNext(false);
    }

    public async Task<Quote?> GetSnapshotAsync(string symbol, CancellationToken ct = default)
    {
        try
        {
            var resp = await _http.GetAsync($"{_config.MarketDataBase}/stocks/{symbol}/snapshot", ct);
            if (!resp.IsSuccessStatusCode) return null;
            var body = await resp.Content.ReadAsStringAsync(ct);
            using var doc = JsonDocument.Parse(body);
            var snapshot = ParseSnapshot(doc.RootElement, symbol);
            if (snapshot != null)
                _latestQuote[symbol] = snapshot; // seed cache so trade ticks can update Last
            return snapshot;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "GetSnapshotAsync failed for {Symbol}", symbol);
            return null;
        }
    }

    public async Task SubscribeQuotesAsync(string symbol, CancellationToken ct = default)
    {
        if (_ws?.State != WebSocketState.Open) return;
        // Subscribe to both quotes (bid/ask) and trades (last price) so the DOM stays live
        var msg = JsonSerializer.Serialize(new
        {
            action = "subscribe",
            quotes = new[] { symbol },
            trades = new[] { symbol },
        });
        await SendWsAsync(msg, ct);
    }

    public async Task UnsubscribeQuotesAsync(string symbol, CancellationToken ct = default)
    {
        if (_ws?.State != WebSocketState.Open) return;
        var msg = JsonSerializer.Serialize(new
        {
            action = "unsubscribe",
            quotes = new[] { symbol },
            trades = new[] { symbol },
        });
        await SendWsAsync(msg, ct);
        _latestQuote.Remove(symbol);
    }

    public Task SubscribeDepthAsync(string symbol, CancellationToken ct = default)
        => Task.CompletedTask; // Alpaca free tier doesn't provide level 2

    public Task UnsubscribeDepthAsync(string symbol, CancellationToken ct = default)
        => Task.CompletedTask;

    private async Task SendWsAsync(string message, CancellationToken ct)
    {
        if (_ws?.State != WebSocketState.Open) return;
        var bytes = Encoding.UTF8.GetBytes(message);
        await _ws.SendAsync(new ArraySegment<byte>(bytes), WebSocketMessageType.Text, true, ct);
    }

    private async Task ReceiveLoopAsync(CancellationToken ct)
    {
        var buffer = new byte[64 * 1024];
        try
        {
            while (!ct.IsCancellationRequested && _ws?.State == WebSocketState.Open)
            {
                var sb = new StringBuilder();
                WebSocketReceiveResult result;
                do
                {
                    result = await _ws.ReceiveAsync(new ArraySegment<byte>(buffer), ct);
                    if (result.MessageType == WebSocketMessageType.Close) return;
                    sb.Append(Encoding.UTF8.GetString(buffer, 0, result.Count));
                } while (!result.EndOfMessage);

                ProcessWsMessage(sb.ToString());
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Alpaca WS receive loop error");
            _connected = false;
            if (!_disposed) _connectionSubject.OnNext(false);
        }
    }

    private void ProcessWsMessage(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Array) return;

            foreach (var item in root.EnumerateArray())
            {
                if (!item.TryGetProperty("T", out var t)) continue;
                var type = t.GetString();
                switch (type)
                {
                    case "q": // bid/ask update — merge into cached quote and emit
                        var q = ParseWsQuote(item);
                        if (q != null)
                        {
                            if (_latestQuote.TryGetValue(q.Symbol, out var prev))
                            {
                                q.Last    = prev.Last;
                                q.Open    = prev.Open;
                                q.Close   = prev.Close;
                                q.High    = prev.High;
                                q.Low     = prev.Low;
                                q.Volume  = prev.Volume;
                            }
                            _latestQuote[q.Symbol] = q;
                            _quoteSubject.OnNext(q);
                        }
                        break;
                    case "t": // trade — update Last on cached quote and emit it
                        var tr = ParseWsTrade(item);
                        if (tr != null)
                        {
                            _tradeSubject.OnNext(tr);
                            if (_latestQuote.TryGetValue(tr.Symbol, out var cached))
                            {
                                cached.Last = tr.Price;
                                _quoteSubject.OnNext(cached);
                            }
                            else
                            {
                                // No quote yet — emit a minimal quote so the DOM has a center price
                                var fromTrade = new Quote
                                {
                                    Symbol       = tr.Symbol,
                                    Last         = tr.Price,
                                    TimestampUtc = tr.TimestampUtc,
                                };
                                _latestQuote[tr.Symbol] = fromTrade;
                                _quoteSubject.OnNext(fromTrade);
                            }
                        }
                        break;
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to parse Alpaca WS message");
        }
    }

    private static Quote? ParseWsQuote(JsonElement e)
    {
        if (!e.TryGetProperty("S", out var sym)) return null;
        return new Quote
        {
            Symbol       = sym.GetString() ?? "",
            Bid          = GetDecimal(e, "bp"),
            Ask          = GetDecimal(e, "ap"),
            BidSize      = GetInt(e, "bs"),
            AskSize      = GetInt(e, "as"),
            TimestampUtc = e.TryGetProperty("t", out var ts)
                            ? DateTime.Parse(ts.GetString() ?? "", null, System.Globalization.DateTimeStyles.RoundtripKind)
                            : DateTime.UtcNow,
        };
    }

    private static Trade? ParseWsTrade(JsonElement e)
    {
        if (!e.TryGetProperty("S", out var sym)) return null;
        return new Trade
        {
            Symbol       = sym.GetString() ?? "",
            Price        = GetDecimal(e, "p"),
            Size         = GetInt(e, "s"),
            TimestampUtc = e.TryGetProperty("t", out var ts)
                            ? DateTime.Parse(ts.GetString() ?? "", null, System.Globalization.DateTimeStyles.RoundtripKind)
                            : DateTime.UtcNow,
        };
    }

    private static Quote? ParseSnapshot(JsonElement root, string symbol)
    {
        if (!root.TryGetProperty("latestQuote", out var q)) return null;
        var t = root.TryGetProperty("latestTrade", out var tr) ? tr : default;
        return new Quote
        {
            Symbol  = symbol,
            Bid     = GetDecimal(q, "bp"),
            Ask     = GetDecimal(q, "ap"),
            BidSize = GetInt(q, "bs"),
            AskSize = GetInt(q, "as"),
            Last    = t.ValueKind != JsonValueKind.Undefined ? GetDecimal(t, "p") : 0,
        };
    }

    private static decimal GetDecimal(JsonElement e, string key)
    {
        if (!e.TryGetProperty(key, out var p)) return 0;
        return p.ValueKind == JsonValueKind.Number ? p.GetDecimal() : 0;
    }

    private static int GetInt(JsonElement e, string key)
    {
        if (!e.TryGetProperty(key, out var p)) return 0;
        return p.ValueKind == JsonValueKind.Number ? p.GetInt32() : 0;
    }

    public async ValueTask DisposeAsync()
    {
        _disposed = true;
        await DisconnectAsync();
        _quoteSubject.Dispose();
        _depthSubject.Dispose();
        _tradeSubject.Dispose();
        _accountActivitySubject.Dispose();
        _connectionSubject.Dispose();
        _http.Dispose();
    }
}
