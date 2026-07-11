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
///   Streaming services: LEVELONE_EQUITIES / LEVELONE_OPTIONS (L1),
///   NYSE_BOOK / NASDAQ_BOOK / OPTIONS_BOOK (L2 depth)
///
/// Depth-of-book is only available through the streaming WebSocket, not REST.
/// </summary>
public class SchwabMarketDataClient : IMarketDataClient
{
    private readonly ILogger<SchwabMarketDataClient> _logger;
    private readonly SchwabConfig _config;
    private readonly SchwabAuthProvider _auth;
    private readonly SchwabAuthProvider _streamerAuth;
    private readonly HttpClient _http;
    private readonly Subject<Quote> _quoteSubject = new();
    private readonly Subject<MarketDepth> _depthSubject = new();
    private readonly Subject<Trade> _tradeSubject = new();
    private readonly Subject<bool> _connectionSubject = new();
    private readonly HashSet<string> _subscribedQuotes = [];
    private readonly HashSet<string> _subscribedDepth = [];
    private readonly Dictionary<string, Quote> _quotesBySymbol = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, MarketDepth> _depthBySymbol = new(StringComparer.OrdinalIgnoreCase);
    private readonly SemaphoreSlim _connectGate = new(1, 1);

    private ClientWebSocket? _ws;
    private CancellationTokenSource? _wsCts;
    private CancellationTokenSource? _snapshotCts;
    private string? _streamerUrl;
    private string? _streamerCustomerId;
    private string? _streamerCorrelId;
    private string? _streamerChannel;
    private string? _streamerFunctionId;
    private bool _connected;
    private int _requestId;
    private int _disposed;

    public bool IsConnected => _connected;
    public bool SupportsLevelTwo => true;
    public IObservable<Quote> QuoteStream => _quoteSubject.AsObservable();
    public IObservable<MarketDepth> DepthStream => _depthSubject.AsObservable();
    public IObservable<Trade> TradeStream => _tradeSubject.AsObservable();
    public IObservable<bool> ConnectionStateStream => _connectionSubject.AsObservable();

    // L1 field indices for LEVELONE_EQUITIES / LEVELONE_OPTIONS
    private static readonly string L1Fields = "0,1,2,3,4,5,6,7,8,9,10,11,12";

    public SchwabMarketDataClient(
        ILogger<SchwabMarketDataClient> logger,
        SchwabConfig config,
        SchwabAuthProvider auth,
        SchwabAuthProvider? streamerAuth = null)
    {
        _logger = logger;
        _config = config;
        _auth = auth;
        _streamerAuth = streamerAuth ?? auth;
        _http = new HttpClient();
    }

    public async Task ConnectAsync(CancellationToken ct = default)
    {
        await _connectGate.WaitAsync(ct);
        try
        {
            if (_ws?.State == WebSocketState.Open)
                return;

            _wsCts?.Cancel();
            _wsCts?.Dispose();
            _ws?.Dispose();

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
                await Task.Delay(1000, _wsCts.Token);
                await ReplayStreamingSubscriptionsAsync(_wsCts.Token);
                StartSnapshotFallbackLoop();
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Schwab WebSocket connect failed");
                _connected = false;
            }
        }
        finally
        {
            _connectGate.Release();
        }
    }

    private async Task<string?> GetStreamerUrlAsync(CancellationToken ct)
    {
        var token = await _streamerAuth.GetAccessTokenAsync(ct);
        if (string.IsNullOrEmpty(token)) return null;

        using var req = new HttpRequestMessage(HttpMethod.Get, $"{_config.TraderApiBase}/userPreference");
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);

        try
        {
            var resp = await _http.SendAsync(req, ct);
            var body = await resp.Content.ReadAsStringAsync(ct);
            using var doc = JsonDocument.Parse(body);
            if (doc.RootElement.TryGetProperty("streamerInfo", out var streamerInfo) &&
                streamerInfo.ValueKind == JsonValueKind.Array &&
                streamerInfo.GetArrayLength() > 0 &&
                streamerInfo[0].TryGetProperty("streamerSocketUrl", out var url))
            {
                var info = streamerInfo[0];
                _streamerCustomerId = TryGetString(info, "schwabClientCustomerId") ?? TryGetString(info, "SchwabClientCustomerId");
                _streamerCorrelId = TryGetString(info, "schwabClientCorrelId") ?? TryGetString(info, "SchwabClientCorrelId");
                _streamerChannel = TryGetString(info, "schwabClientChannel") ?? TryGetString(info, "SchwabClientChannel") ?? "N9";
                _streamerFunctionId = TryGetString(info, "schwabClientFunctionId") ?? TryGetString(info, "SchwabClientFunctionId") ?? "APIAPP";
                return url.GetString();
            }

            _logger.LogError("Schwab userPreference did not include streamerInfo. Status={Status} Body={Body}", resp.StatusCode, body);
            return null;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to get streamer URL");
            return null;
        }
    }

    private async Task SendAdminLoginAsync(CancellationToken ct)
    {
        var token = await _streamerAuth.GetAccessTokenAsync(ct);
        if (string.IsNullOrWhiteSpace(_streamerCustomerId) || string.IsNullOrWhiteSpace(_streamerCorrelId))
            _logger.LogWarning("Schwab streamer identifiers missing from userPreference; login may not deliver live ticks");

        var cmd = new
        {
            requests = new[]
            {
                new
                {
                    service = "ADMIN",
                    command = "LOGIN",
                    requestid = Interlocked.Increment(ref _requestId),
                    SchwabClientCustomerId = _streamerCustomerId ?? "fastdom",
                    SchwabClientCorrelId = _streamerCorrelId ?? Guid.NewGuid().ToString("N"),
                    parameters = new
                    {
                        Authorization = token,
                        SchwabClientChannel = _streamerChannel ?? "N9",
                        SchwabClientFunctionId = _streamerFunctionId ?? "APIAPP"
                    }
                }
            }
        };

        await SendAsync(JsonSerializer.Serialize(cmd), ct);
    }

    public async Task SubscribeQuotesAsync(string symbol, CancellationToken ct = default)
    {
        symbol = NormalizeDisplaySymbol(symbol);
        lock (_subscribedQuotes)
        {
            _subscribedQuotes.Add(symbol);
        }
        StartSnapshotFallbackLoop();

        if (_ws?.State != WebSocketState.Open)
            await ConnectAsync(ct);

        if (_ws?.State == WebSocketState.Open)
        {
            var service = IsOptionSymbol(symbol) ? "LEVELONE_OPTIONS" : "LEVELONE_EQUITIES";
            await SendSubscribeAsync(service, ToStreamerKey(symbol), L1Fields, ct);
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
        var quotes = await GetSnapshotsAsync([NormalizeDisplaySymbol(symbol)], ct);
        return quotes.FirstOrDefault();
    }

    private async Task<IReadOnlyList<Quote>> GetSnapshotsAsync(IReadOnlyCollection<string> symbols, CancellationToken ct = default)
    {
        var token = await _auth.GetAccessTokenAsync(ct);
        if (string.IsNullOrEmpty(token)) return [];

        var normalized = symbols
            .Select(NormalizeDisplaySymbol)
            .Where(s => !string.IsNullOrWhiteSpace(s))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        if (normalized.Length == 0) return [];

        var requestKeys = normalized
            .ToDictionary(s => s, ToStreamerKey, StringComparer.OrdinalIgnoreCase);

        using var req = new HttpRequestMessage(HttpMethod.Get,
            $"{_config.MarketDataApiBase}/quotes?symbols={Uri.EscapeDataString(string.Join(",", requestKeys.Values))}&fields=quote,fundamental");
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);

        try
        {
            var resp = await _http.SendAsync(req, ct);
            var body = await resp.Content.ReadAsStringAsync(ct);
            if (!resp.IsSuccessStatusCode)
            {
                _logger.LogWarning("Schwab quote snapshot failed: {Status} {Body}", (int)resp.StatusCode, body);
                return [];
            }

            using var doc = JsonDocument.Parse(body);
            var result = new List<Quote>(normalized.Length);
            foreach (var symbol in normalized)
            {
                if (!TryGetQuoteElement(doc.RootElement, symbol, requestKeys[symbol], out var sym)) continue;
                if (!sym.TryGetProperty("quote", out var q)) continue;

                result.Add(new Quote
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
                });
            }

            return result;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Snapshot request failed for {Symbols}", string.Join(",", symbols));
            return [];
        }
    }

    private static bool TryGetQuoteElement(JsonElement root, string normalizedSymbol, string requestKey, out JsonElement quoteElement)
    {
        if (root.TryGetProperty(normalizedSymbol, out quoteElement))
            return true;

        if (root.TryGetProperty(requestKey, out quoteElement))
            return true;

        foreach (var property in root.EnumerateObject())
        {
            if (string.Equals(NormalizeDisplaySymbol(property.Name), normalizedSymbol, StringComparison.OrdinalIgnoreCase))
            {
                quoteElement = property.Value;
                return true;
            }
        }

        quoteElement = default;
        return false;
    }

    public async Task SubscribeDepthAsync(string symbol, CancellationToken ct = default)
    {
        symbol = NormalizeDisplaySymbol(symbol);
        lock (_subscribedDepth)
        {
            _subscribedDepth.Add(symbol);
        }

        if (_ws?.State != WebSocketState.Open)
            await ConnectAsync(ct);

        if (_ws?.State == WebSocketState.Open)
        {
            await SendDepthSubscribeAsync(symbol, ct);
        }
    }

    private async Task ReplayStreamingSubscriptionsAsync(CancellationToken ct)
    {
        string[] quoteSymbols;
        string[] depthSymbols;
        lock (_subscribedQuotes)
        {
            quoteSymbols = _subscribedQuotes.ToArray();
        }
        lock (_subscribedDepth)
        {
            depthSymbols = _subscribedDepth.ToArray();
        }

        foreach (var symbol in quoteSymbols)
        {
            var service = IsOptionSymbol(symbol) ? "LEVELONE_OPTIONS" : "LEVELONE_EQUITIES";
            await SendSubscribeAsync(service, ToStreamerKey(symbol), L1Fields, ct);
        }

        foreach (var symbol in depthSymbols)
            await SendDepthSubscribeAsync(symbol, ct);
    }

    private async Task SendDepthSubscribeAsync(string symbol, CancellationToken ct)
    {
        var key = ToStreamerKey(symbol);
        if (IsOptionSymbol(symbol))
        {
            await SendSubscribeAsync("OPTIONS_BOOK", key, "0,1,2,3", ct);
            return;
        }

        // Use both equity books; Schwab will deliver the matching venue.
        await SendSubscribeAsync("NYSE_BOOK", key, "0,1,2,3", ct);
        await SendSubscribeAsync("NASDAQ_BOOK", key, "0,1,2,3", ct);
    }

    private void StartSnapshotFallbackLoop()
    {
        if (_snapshotCts != null) return;
        _snapshotCts = new CancellationTokenSource();
        _ = Task.Run(() => SnapshotFallbackLoopAsync(_snapshotCts.Token));
    }

    private async Task SnapshotFallbackLoopAsync(CancellationToken ct)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(1));
        while (!ct.IsCancellationRequested && await timer.WaitForNextTickAsync(ct).ConfigureAwait(false))
        {
            string[] symbols;
            lock (_subscribedQuotes)
            {
                symbols = _subscribedQuotes.ToArray();
            }

            if (symbols.Length == 0)
                continue;

            try
            {
                var quotes = await GetSnapshotsAsync(symbols, ct).ConfigureAwait(false);
                foreach (var quote in quotes)
                    PublishQuote(quote, source: "REST");
            }
            catch (OperationCanceledException) { }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Schwab snapshot fallback loop failed");
            }
        }
    }

    public Task UnsubscribeQuotesAsync(string symbol, CancellationToken ct = default)
    {
        symbol = NormalizeDisplaySymbol(symbol);
        lock (_subscribedQuotes)
        {
            _subscribedQuotes.Remove(symbol);
        }
        return Task.CompletedTask;
    }

    public Task UnsubscribeDepthAsync(string symbol, CancellationToken ct = default)
    {
        symbol = NormalizeDisplaySymbol(symbol);
        lock (_subscribedDepth)
        {
            _subscribedDepth.Remove(symbol);
        }
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
                    SchwabClientCustomerId = _streamerCustomerId ?? "fastdom",
                    SchwabClientCorrelId = _streamerCorrelId ?? Guid.NewGuid().ToString("N"),
                    parameters = new { keys = symbol, fields }
                }
            }
        };
        _logger.LogInformation("Schwab stream subscribe {Service} {Symbol} fields={Fields}", service, symbol, fields);
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
                    if (service is "LEVELONE_EQUITIES" or "LEVELONE_OPTIONS")
                        ParseL1Data(item);
                    else if (service is "LISTED_BOOK" or "NYSE_BOOK" or "NASDAQ_BOOK" or "OPTIONS_BOOK")
                        ParseDepthData(item);
                }
            }

            if (root.TryGetProperty("response", out var response))
            {
                foreach (var item in response.EnumerateArray())
                {
                    var service = item.TryGetProperty("service", out var s) ? s.GetString() : "";
                    var command = item.TryGetProperty("command", out var c) ? c.GetString() : "";
                    var code = item.TryGetProperty("content", out var content) && content.TryGetProperty("code", out var codeElement)
                        ? TryReadInt(codeElement, out var codeValue) ? codeValue : 0
                        : 0;
                    var message = item.TryGetProperty("content", out content) && content.TryGetProperty("msg", out var msgElement)
                        ? msgElement.GetString()
                        : "";
                    _logger.LogInformation("Schwab stream response {Service} {Command} code={Code} {Message}", service, command, code, message);

                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Stream message parse error");
        }
    }

    private void ParseL1Data(JsonElement item)
    {
        if (!item.TryGetProperty("content", out var content)) return;
        foreach (var c in content.EnumerateArray())
        {
            var symbol = c.TryGetProperty("key", out var k) ? NormalizeDisplaySymbol(k.GetString() ?? "") : "";
            if (string.IsNullOrWhiteSpace(symbol)) continue;

            _quotesBySymbol.TryGetValue(symbol, out var previous);
            var quote = previous != null ? CopyQuote(previous) : new Quote { Symbol = symbol };

            // Field mapping: 1=bid, 2=ask, 3=last, 9=volume, ...
            if (TryGetDecimal(c, "1", out var bid) && bid > 0) quote.Bid = bid;
            if (TryGetDecimal(c, "2", out var ask) && ask > 0) quote.Ask = ask;
            if (TryGetDecimal(c, "3", out var last) && last > 0) quote.Last = last;
            if (TryGetInt(c, "4", out var bidSize)) quote.BidSize = bidSize;
            if (TryGetInt(c, "5", out var askSize)) quote.AskSize = askSize;
            if (TryGetLong(c, "8", out var vol)) quote.Volume = vol;

            // Schwab stream messages are partial. Do not publish an unusable
            // quote that would recenter the DOM on zero.
            if (quote.Last <= 0 && quote.Bid <= 0 && quote.Ask <= 0)
                continue;

            PublishQuote(quote, source: "WS");
        }
    }

    private void PublishQuote(Quote quote, string source)
    {
        quote.TimestampUtc = DateTime.UtcNow;
        _quotesBySymbol[quote.Symbol] = quote;
        _quoteSubject.OnNext(quote);
        _logger.LogDebug("Schwab quote {Source} {Symbol}: last={Last} bid={Bid} ask={Ask}", source, quote.Symbol, quote.Last, quote.Bid, quote.Ask);
    }

    private void ParseDepthData(JsonElement item)
    {
        if (!item.TryGetProperty("content", out var content)) return;
        foreach (var c in content.EnumerateArray())
        {
            var symbol = c.TryGetProperty("key", out var k) ? NormalizeDisplaySymbol(k.GetString() ?? "") : "";
            if (string.IsNullOrWhiteSpace(symbol)) continue;

            if (!_depthBySymbol.TryGetValue(symbol, out var depth))
            {
                depth = new MarketDepth { Symbol = symbol, HasRealDepth = true };
                _depthBySymbol[symbol] = depth;
            }

            if (c.TryGetProperty("2", out var bids))
                ApplyBookLevels(bids, depth.Bids, isBid: true);
            if (c.TryGetProperty("3", out var asks))
                ApplyBookLevels(asks, depth.Asks, isBid: false);

            depth.TimestampUtc = DateTime.UtcNow;
            depth.HasRealDepth = true;
            if (depth.Bids.Count > 0 || depth.Asks.Count > 0)
            {
                _logger.LogDebug("Schwab depth {Symbol}: bids={BidCount} asks={AskCount}", symbol, depth.Bids.Count, depth.Asks.Count);
                _depthSubject.OnNext(CloneDepth(depth));
            }
        }
    }

    private static MarketDepth CloneDepth(MarketDepth depth) => new()
    {
        Symbol = depth.Symbol,
        TimestampUtc = depth.TimestampUtc,
        HasRealDepth = depth.HasRealDepth,
        Bids = depth.Bids.Select(x => new DomLevel { Price = x.Price, BidSize = x.BidSize }).ToList(),
        Asks = depth.Asks.Select(x => new DomLevel { Price = x.Price, AskSize = x.AskSize }).ToList()
    };

    private static void ApplyBookLevels(JsonElement levels, List<DomLevel> target, bool isBid)
    {
        if (levels.ValueKind != JsonValueKind.Array) return;

        foreach (var level in levels.EnumerateArray())
        {
            if (level.ValueKind != JsonValueKind.Object) continue;
            var price = level.TryGetProperty("0", out var p) && TryReadDecimal(p, out var priceValue)
                ? priceValue
                : 0m;
            var size = level.TryGetProperty("1", out var s) && TryReadInt(s, out var sizeValue)
                ? sizeValue
                : 0;
            if (price <= 0) continue;

            var existing = target.FirstOrDefault(x => x.Price == price);
            if (size <= 0)
            {
                if (existing != null)
                    target.Remove(existing);
                continue;
            }

            if (existing == null)
            {
                existing = new DomLevel { Price = price };
                target.Add(existing);
            }

            if (isBid) existing.BidSize = size;
            else existing.AskSize = size;
        }

        if (isBid)
            target.Sort((a, b) => b.Price.CompareTo(a.Price));
        else
            target.Sort((a, b) => a.Price.CompareTo(b.Price));

        if (target.Count > 50)
            target.RemoveRange(50, target.Count - 50);
    }

    private static Quote CopyQuote(Quote q) => new()
    {
        Symbol = q.Symbol,
        Bid = q.Bid,
        Ask = q.Ask,
        Last = q.Last,
        BidSize = q.BidSize,
        AskSize = q.AskSize,
        LastSize = q.LastSize,
        Volume = q.Volume,
        Open = q.Open,
        High = q.High,
        Low = q.Low,
        Close = q.Close,
        NetChange = q.NetChange,
        NetChangePct = q.NetChangePct,
    };

    private static bool IsOptionSymbol(string symbol)
    {
        symbol = symbol.Trim().ToUpperInvariant();
        return TrySplitOptionSymbol(symbol, out _, out _);
    }

    private static string ToStreamerKey(string symbol)
    {
        symbol = NormalizeDisplaySymbol(symbol);
        return TrySplitOptionSymbol(symbol, out var root, out var suffix)
            ? root.PadRight(6) + suffix
            : symbol;
    }

    private static string NormalizeDisplaySymbol(string symbol)
    {
        symbol = symbol.Trim().ToUpperInvariant();
        return TrySplitOptionSymbol(symbol, out var root, out var suffix)
            ? root.Trim() + suffix
            : symbol;
    }

    private static bool TrySplitOptionSymbol(string symbol, out string root, out string suffix)
    {
        symbol = symbol.Trim().ToUpperInvariant();
        root = "";
        suffix = "";

        for (var i = 1; i <= Math.Min(6, symbol.Length - 15); i++)
        {
            var candidateSuffix = symbol[i..].TrimStart();
            if (candidateSuffix.Length != 15) continue;
            if (!candidateSuffix[..6].All(char.IsDigit)) continue;
            if (candidateSuffix[6] is not ('C' or 'P')) continue;
            if (!candidateSuffix[7..].All(char.IsDigit)) continue;

            root = symbol[..i].Trim();
            suffix = candidateSuffix;
            return root.Length > 0;
        }

        return false;
    }

    private static bool TryGetDecimal(JsonElement source, string property, out decimal value)
    {
        value = 0;
        return source.TryGetProperty(property, out var element) && TryReadDecimal(element, out value);
    }

    private static bool TryGetInt(JsonElement source, string property, out int value)
    {
        value = 0;
        return source.TryGetProperty(property, out var element) && TryReadInt(element, out value);
    }

    private static bool TryGetLong(JsonElement source, string property, out long value)
    {
        value = 0;
        if (!source.TryGetProperty(property, out var element)) return false;
        if (element.ValueKind == JsonValueKind.Number) return element.TryGetInt64(out value);
        return element.ValueKind == JsonValueKind.String && long.TryParse(element.GetString(), out value);
    }

    private static bool TryReadDecimal(JsonElement element, out decimal value)
    {
        value = 0;
        if (element.ValueKind == JsonValueKind.Number) return element.TryGetDecimal(out value);
        return element.ValueKind == JsonValueKind.String && decimal.TryParse(element.GetString(), out value);
    }

    private static bool TryReadInt(JsonElement element, out int value)
    {
        value = 0;
        if (element.ValueKind == JsonValueKind.Number)
        {
            if (element.TryGetInt32(out value)) return true;
            if (element.TryGetDecimal(out var decimalValue))
            {
                value = Convert.ToInt32(decimalValue);
                return true;
            }
        }
        return element.ValueKind == JsonValueKind.String && int.TryParse(element.GetString(), out value);
    }

    private static string? TryGetString(JsonElement item, string property) =>
        item.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    public async Task DisconnectAsync(CancellationToken ct = default)
    {
        if (_disposed != 0) return;

        CancelAndDispose(ref _snapshotCts);
        CancelAndDispose(ref _wsCts);
        if (_ws?.State == WebSocketState.Open)
            await _ws.CloseAsync(WebSocketCloseStatus.NormalClosure, "Disconnect", CancellationToken.None);
        _logger.LogWarning("Schwab streaming WebSocket disconnected. State={State}", _ws?.State);
        _connected = false;
        _connectionSubject.OnNext(false);
    }

    public ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
            return ValueTask.CompletedTask;

        CancelAndDispose(ref _snapshotCts);
        CancelAndDispose(ref _wsCts);
        _ws?.Dispose();
        _http.Dispose();
        _quoteSubject.Dispose();
        _depthSubject.Dispose();
        _tradeSubject.Dispose();
        _connectionSubject.Dispose();
        return ValueTask.CompletedTask;
    }

    private static void CancelAndDispose(ref CancellationTokenSource? cts)
    {
        var current = Interlocked.Exchange(ref cts, null);
        if (current == null) return;

        try { current.Cancel(); }
        catch (ObjectDisposedException) { }
        finally { current.Dispose(); }
    }
}
