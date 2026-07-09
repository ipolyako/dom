using System.Net.Http.Headers;
using System.Net.WebSockets;
using System.Reactive.Linq;
using System.Reactive.Subjects;
using System.Text;
using System.Text.Json;
using FastDOM.Broker.Schwab.Auth;
using FastDOM.Infrastructure.Config;
using FastDOM.MarketData.Interfaces;
using FastDOM.MarketData.Models;
using Microsoft.Extensions.Logging;

namespace FastDOM.Broker.Schwab.Client;

/// <summary>
/// Schwab Market Data client.
///
/// Confirmed endpoints:
///   REST L1:  GET /marketdata/v1/quotes?symbols=SPY,NVDA  (single snapshot)
///   Streaming: WebSocket URL from GET /trader/v1/userPreference → streamerInfo[0].streamerSocketUrl
///   Streaming services: LEVELONE_EQUITIES (L1), NYSE_BOOK / NASDAQ_BOOK (L2 depth)
///
/// Depth-of-book is only available through the streaming WebSocket, not REST.
/// </summary>
public class SchwabMarketDataClient : IMarketDataClient
{
    private readonly ILogger<SchwabMarketDataClient> _logger;
    private readonly SchwabConfig _config;
    private readonly SchwabAuthProvider _auth;
    private readonly HttpClient _http;
    private readonly Subject<Quote> _quoteSubject = new();
    private readonly Subject<MarketDepth> _depthSubject = new();
    private readonly Subject<Trade> _tradeSubject = new();
    private readonly Subject<bool> _connectionSubject = new();
    private readonly HashSet<string> _subscribedQuotes = [];
    private readonly HashSet<string> _subscribedDepth = [];

    private ClientWebSocket? _ws;
    private CancellationTokenSource? _wsCts;
    private string? _streamerUrl;
    private bool _connected;
    private int _requestId;

    public bool IsConnected => _connected;
    public bool SupportsLevelTwo => true;
    public IObservable<Quote> QuoteStream => _quoteSubject.AsObservable();
    public IObservable<MarketDepth> DepthStream => _depthSubject.AsObservable();
    public IObservable<Trade> TradeStream => _tradeSubject.AsObservable();
    public IObservable<bool> ConnectionStateStream => _connectionSubject.AsObservable();

    // L1 field indices for LEVELONE_EQUITIES
    private static readonly string L1Fields = "0,1,2,3,4,5,6,7,8,9,10,11,12";

    public SchwabMarketDataClient(
        ILogger<SchwabMarketDataClient> logger,
        SchwabConfig config,
        SchwabAuthProvider auth)
    {
        _logger = logger;
        _config = config;
        _auth = auth;
        _http = new HttpClient();
    }

    public async Task ConnectAsync(CancellationToken ct = default)
    {
        _streamerUrl = await GetStreamerUrlAsync(ct);
        if (string.IsNullOrEmpty(_streamerUrl))
        {
            _logger.LogError("Could not get Schwab streamer URL");
            return;
        }

        _ws = new ClientWebSocket();
        _wsCts = CancellationTokenSource.CreateLinkedTokenSource(ct);

        try
        {
            await _ws.ConnectAsync(new Uri(_streamerUrl), _wsCts.Token);
            _connected = true;
            _connectionSubject.OnNext(true);
            _logger.LogInformation("Schwab streaming WebSocket connected");

            // Start receive loop
            _ = Task.Run(() => ReceiveLoopAsync(_wsCts.Token), _wsCts.Token);

            // Send ADMIN/LOGIN
            await SendAdminLoginAsync(_wsCts.Token);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Schwab WebSocket connect failed");
            _connected = false;
        }
    }

    private async Task<string?> GetStreamerUrlAsync(CancellationToken ct)
    {
        var token = await _auth.GetAccessTokenAsync(ct);
        if (string.IsNullOrEmpty(token)) return null;

        using var req = new HttpRequestMessage(HttpMethod.Get, $"{_config.TraderApiBase}/userPreference");
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);

        try
        {
            var resp = await _http.SendAsync(req, ct);
            var body = await resp.Content.ReadAsStringAsync(ct);
            using var doc = JsonDocument.Parse(body);
            return doc.RootElement
                .GetProperty("streamerInfo")[0]
                .GetProperty("streamerSocketUrl")
                .GetString();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to get streamer URL");
            return null;
        }
    }

    private async Task SendAdminLoginAsync(CancellationToken ct)
    {
        var token = await _auth.GetAccessTokenAsync(ct);
        var cmd = new
        {
            requests = new[]
            {
                new
                {
                    service = "ADMIN",
                    command = "LOGIN",
                    requestid = Interlocked.Increment(ref _requestId),
                    SchwabClientCustomerId = "fastdom",
                    SchwabClientCorrelId = Guid.NewGuid().ToString("N"),
                    parameters = new
                    {
                        Authorization = token,
                        SchwabClientChannel = "N9",
                        SchwabClientFunctionId = "APIAPP"
                    }
                }
            }
        };

        await SendAsync(JsonSerializer.Serialize(cmd), ct);
    }

    public async Task SubscribeQuotesAsync(string symbol, CancellationToken ct = default)
    {
        symbol = symbol.ToUpperInvariant();
        _subscribedQuotes.Add(symbol);

        if (_ws?.State == WebSocketState.Open)
        {
            await SendSubscribeAsync("LEVELONE_EQUITIES", symbol, L1Fields, ct);
        }
        else
        {
            // Fallback to REST snapshot
            var quote = await GetSnapshotAsync(symbol, ct);
            if (quote != null) _quoteSubject.OnNext(quote);
        }
    }

    public async Task<Quote?> GetSnapshotAsync(string symbol, CancellationToken ct = default)
    {
        var token = await _auth.GetAccessTokenAsync(ct);
        if (string.IsNullOrEmpty(token)) return null;

        using var req = new HttpRequestMessage(HttpMethod.Get,
            $"{_config.MarketDataApiBase}/quotes?symbols={Uri.EscapeDataString(symbol)}&fields=quote,fundamental");
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);

        try
        {
            var resp = await _http.SendAsync(req, ct);
            var body = await resp.Content.ReadAsStringAsync(ct);
            using var doc = JsonDocument.Parse(body);

            if (!doc.RootElement.TryGetProperty(symbol, out var sym)) return null;
            if (!sym.TryGetProperty("quote", out var q)) return null;

            return new Quote
            {
                Symbol = symbol,
                Bid    = q.TryGetProperty("bidPrice", out var b) ? b.GetDecimal() : 0,
                Ask    = q.TryGetProperty("askPrice", out var a) ? a.GetDecimal() : 0,
                Last   = q.TryGetProperty("lastPrice", out var l) ? l.GetDecimal() : 0,
                BidSize = q.TryGetProperty("bidSize", out var bs) ? bs.GetInt32() : 0,
                AskSize = q.TryGetProperty("askSize", out var as2) ? as2.GetInt32() : 0,
                Volume = q.TryGetProperty("totalVolume", out var v) ? v.GetInt64() : 0,
                Open   = q.TryGetProperty("openPrice", out var op) ? op.GetDecimal() : 0,
                Close  = q.TryGetProperty("closePrice", out var cp) ? cp.GetDecimal() : 0,
                High   = q.TryGetProperty("highPrice", out var hp) ? hp.GetDecimal() : 0,
                Low    = q.TryGetProperty("lowPrice", out var lp) ? lp.GetDecimal() : 0,
                NetChange = q.TryGetProperty("netChange", out var nc) ? nc.GetDecimal() : 0,
                NetChangePct = q.TryGetProperty("netPercentChange", out var ncp) ? ncp.GetDecimal() : 0,
            };
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Snapshot request failed for {Symbol}", symbol);
            return null;
        }
    }

    public async Task SubscribeDepthAsync(string symbol, CancellationToken ct = default)
    {
        symbol = symbol.ToUpperInvariant();
        _subscribedDepth.Add(symbol);

        if (_ws?.State == WebSocketState.Open)
        {
            // Use NYSE_BOOK or NASDAQ_BOOK based on exchange
            await SendSubscribeAsync("NYSE_BOOK", symbol, "0,1,2,3", ct);
            await SendSubscribeAsync("NASDAQ_BOOK", symbol, "0,1,2,3", ct);
        }
    }

    public Task UnsubscribeQuotesAsync(string symbol, CancellationToken ct = default)
    {
        _subscribedQuotes.Remove(symbol.ToUpperInvariant());
        return Task.CompletedTask;
    }

    public Task UnsubscribeDepthAsync(string symbol, CancellationToken ct = default)
    {
        _subscribedDepth.Remove(symbol.ToUpperInvariant());
        return Task.CompletedTask;
    }

    private async Task SendSubscribeAsync(string service, string symbol, string fields, CancellationToken ct)
    {
        var cmd = new
        {
            requests = new[]
            {
                new
                {
                    service,
                    command = "SUBS",
                    requestid = Interlocked.Increment(ref _requestId),
                    SchwabClientCustomerId = "fastdom",
                    SchwabClientCorrelId = Guid.NewGuid().ToString("N"),
                    parameters = new { keys = symbol, fields }
                }
            }
        };
        await SendAsync(JsonSerializer.Serialize(cmd), ct);
    }

    private async Task SendAsync(string json, CancellationToken ct)
    {
        if (_ws?.State != WebSocketState.Open) return;
        var bytes = Encoding.UTF8.GetBytes(json);
        await _ws.SendAsync(bytes, WebSocketMessageType.Text, true, ct);
    }

    private async Task ReceiveLoopAsync(CancellationToken ct)
    {
        var buffer = new byte[65536];
        var sb = new StringBuilder();

        try
        {
            while (_ws?.State == WebSocketState.Open && !ct.IsCancellationRequested)
            {
                sb.Clear();
                WebSocketReceiveResult result;
                do
                {
                    result = await _ws.ReceiveAsync(buffer, ct);
                    sb.Append(Encoding.UTF8.GetString(buffer, 0, result.Count));
                } while (!result.EndOfMessage);

                if (result.MessageType == WebSocketMessageType.Close) break;
                ParseStreamMessage(sb.ToString());
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Schwab WebSocket receive error");
        }

        _connected = false;
        _connectionSubject.OnNext(false);
    }

    private void ParseStreamMessage(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;

            if (root.TryGetProperty("data", out var data))
            {
                foreach (var item in data.EnumerateArray())
                {
                    var service = item.TryGetProperty("service", out var s) ? s.GetString() : "";
                    if (service == "LEVELONE_EQUITIES")
                        ParseL1Data(item);
                    else if (service is "NYSE_BOOK" or "NASDAQ_BOOK")
                        ParseDepthData(item);
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Stream message parse error");
        }
    }

    // Schwab's LEVELONE_EQUITIES stream sends PARTIAL updates — a message may
    // include only the fields that changed. If we build a fresh Quote per
    // message the unset fields default to 0 and the ladder centers on 0,
    // showing negative prices. Merge each partial into a per-symbol running
    // Quote and emit the merged copy.
    private readonly Dictionary<string, Quote> _latestL1 = new();

    private void ParseL1Data(JsonElement item)
    {
        if (!item.TryGetProperty("content", out var content)) return;
        foreach (var c in content.EnumerateArray())
        {
            var symbol = c.TryGetProperty("key", out var k) ? k.GetString() ?? "" : "";
            if (string.IsNullOrEmpty(symbol)) continue;

            if (!_latestL1.TryGetValue(symbol, out var running))
            {
                running = new Quote { Symbol = symbol };
                _latestL1[symbol] = running;
            }

            // Field mapping (Schwab LEVELONE_EQUITIES): 1=bid, 2=ask, 3=last, 4=bidSize, 5=askSize, 8=volume, ...
            if (c.TryGetProperty("1", out var bid))     running.Bid     = bid.GetDecimal();
            if (c.TryGetProperty("2", out var ask))     running.Ask     = ask.GetDecimal();
            if (c.TryGetProperty("3", out var last))    running.Last    = last.GetDecimal();
            if (c.TryGetProperty("4", out var bidSize)) running.BidSize = (int)bidSize.GetDecimal();
            if (c.TryGetProperty("5", out var askSize)) running.AskSize = (int)askSize.GetDecimal();
            if (c.TryGetProperty("8", out var vol))     running.Volume  = (long)vol.GetDecimal();
            running.TimestampUtc = DateTime.UtcNow;

            // Emit only when we have a meaningful center price for the ladder.
            // Suppresses the initial volume-only partial that would otherwise
            // center the DOM on $0.00.
            if (running.Last > 0 || running.Bid > 0 || running.Ask > 0)
                _quoteSubject.OnNext(running);
        }
    }

    private void ParseDepthData(JsonElement item)
    {
        if (!item.TryGetProperty("content", out var content)) return;
        foreach (var c in content.EnumerateArray())
        {
            var symbol = c.TryGetProperty("key", out var k) ? k.GetString() ?? "" : "";
            var depth = new MarketDepth { Symbol = symbol, HasRealDepth = true };
            if (c.TryGetProperty("1", out var bids))
                foreach (var bid in bids.EnumerateArray())
                    depth.Bids.Add(new DomLevel
                    {
                        Price = bid.TryGetProperty("0", out var p) ? p.GetDecimal() : 0,
                        BidSize = bid.TryGetProperty("1", out var s) ? s.GetInt32() : 0
                    });
            if (c.TryGetProperty("2", out var asks))
                foreach (var ask in asks.EnumerateArray())
                    depth.Asks.Add(new DomLevel
                    {
                        Price = ask.TryGetProperty("0", out var p) ? p.GetDecimal() : 0,
                        AskSize = ask.TryGetProperty("1", out var s) ? s.GetInt32() : 0
                    });
            _depthSubject.OnNext(depth);
        }
    }

    public async Task DisconnectAsync(CancellationToken ct = default)
    {
        _wsCts?.Cancel();
        if (_ws?.State == WebSocketState.Open)
            await _ws.CloseAsync(WebSocketCloseStatus.NormalClosure, "Disconnect", CancellationToken.None);
        _connected = false;
        _connectionSubject.OnNext(false);
    }

    public ValueTask DisposeAsync()
    {
        _wsCts?.Dispose();
        _ws?.Dispose();
        _http.Dispose();
        _quoteSubject.Dispose();
        _depthSubject.Dispose();
        _tradeSubject.Dispose();
        _connectionSubject.Dispose();
        return ValueTask.CompletedTask;
    }
}
