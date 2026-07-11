using System.Net.Http.Headers;
using System.Net.WebSockets;
using System.Reactive.Linq;
using System.Reactive.Subjects;
using System.Text;
using System.Text.Json;
using System.Collections.Concurrent;
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
public class SchwabMarketDataClient : IMarketDataClient, IMarketMoversClient
{
    private readonly ILogger<SchwabMarketDataClient> _logger;
    private readonly SchwabConfig _config;
    private readonly SchwabAuthProvider _auth;
    private readonly SchwabAuthProvider _streamerAuth;
    private readonly HttpClient _http;
    private readonly Subject<Quote> _quoteSubject = new();
    private readonly Subject<MarketDepth> _depthSubject = new();
    private readonly Subject<Trade> _tradeSubject = new();
    private readonly Subject<AccountActivity> _accountActivitySubject = new();
    private readonly Subject<bool> _connectionSubject = new();
    private readonly HashSet<string> _subscribedQuotes = [];
    private readonly HashSet<string> _subscribedDepth = [];
    private readonly Dictionary<string, Quote> _quotesBySymbol = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, MarketDepth> _depthBySymbol = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, MarketDepth> _depthByVenueSymbol = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<int, TaskCompletionSource<StreamResponse>> _pendingRequests = new();
    private readonly ConcurrentDictionary<string, DateTime> _lastWsQuoteUtc = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, long> _lastAccountSequence = new(StringComparer.OrdinalIgnoreCase);
    private readonly SemaphoreSlim _connectGate = new(1, 1);
    private readonly SemaphoreSlim _sendGate = new(1, 1);

    private ClientWebSocket? _ws;
    private CancellationTokenSource? _wsCts;
    private CancellationTokenSource? _snapshotCts;
    private string? _streamerUrl;
    private string? _streamerCustomerId;
    private string? _streamerCorrelId;
    private string? _streamerChannel;
    private string? _streamerFunctionId;
    private bool _connected;
    private bool _supportsLevelTwo;
    private bool _intentionalDisconnect;
    private DateTime _lastHeartbeatUtc = DateTime.MinValue;
    private int _requestId;
    private int _disposed;

    public bool IsConnected => _connected;
    public bool SupportsLevelTwo => _supportsLevelTwo;
    public IObservable<Quote> QuoteStream => _quoteSubject.AsObservable();
    public IObservable<MarketDepth> DepthStream => _depthSubject.AsObservable();
    public IObservable<Trade> TradeStream => _tradeSubject.AsObservable();
    public IObservable<AccountActivity> AccountActivityStream => _accountActivitySubject.AsObservable();
    public IObservable<bool> ConnectionStateStream => _connectionSubject.AsObservable();

    // L1 field indices for LEVELONE_EQUITIES / LEVELONE_OPTIONS
    internal const string EquityFields = "0,1,2,3,4,5,8,9,10,11,12,17,18,42";
    internal const string OptionFields = "0,2,3,4,5,6,7,8,15,16,17,18,30,31,32";

    private sealed record StreamResponse(int RequestId, string Service, string Command, int Code, string Message);

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
            if (_connected && _ws?.State == WebSocketState.Open)
                return;

            _intentionalDisconnect = false;
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
            // The socket lifetime must not be tied to a short-lived UI command token.
            _wsCts = new CancellationTokenSource();

            try
            {
                await _ws.ConnectAsync(new Uri(_streamerUrl), _wsCts.Token);
                _logger.LogInformation("Schwab streaming WebSocket connected");

                // Start receive loop
                _ = Task.Run(() => ReceiveLoopAsync(_wsCts.Token), _wsCts.Token);

                // Send ADMIN/LOGIN
                var login = await SendAdminLoginAsync(_wsCts.Token);
                if (login.Code != 0)
                    throw new InvalidOperationException($"Schwab streamer login failed ({login.Code}): {login.Message}");

                _connected = true;
                _lastHeartbeatUtc = DateTime.UtcNow;
                _connectionSubject.OnNext(true);
                await ReplayStreamingSubscriptionsAsync(_wsCts.Token);
                try { await SubscribeAccountActivityAsync(_wsCts.Token); }
                catch (Exception ex) { _logger.LogWarning(ex, "Schwab account activity stream unavailable"); }
                StartSnapshotFallbackLoop();
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Schwab WebSocket connect failed");
                _connected = false;
                _connectionSubject.OnNext(false);
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
            if (!resp.IsSuccessStatusCode)
            {
                _logger.LogError("Schwab userPreference failed: {Status} {Body}", (int)resp.StatusCode, body);
                return null;
            }
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

    private async Task<StreamResponse> SendAdminLoginAsync(CancellationToken ct)
    {
        var token = await _streamerAuth.GetAccessTokenAsync(ct);
        if (string.IsNullOrWhiteSpace(_streamerCustomerId) || string.IsNullOrWhiteSpace(_streamerCorrelId))
            _logger.LogWarning("Schwab streamer identifiers missing from userPreference; login may not deliver live ticks");

        var requestId = Interlocked.Increment(ref _requestId);
        var cmd = new
        {
            requests = new[]
            {
                new
                {
                    service = "ADMIN",
                    command = "LOGIN",
                    requestid = requestId,
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

        return await SendAndAwaitResponseAsync(requestId, JsonSerializer.Serialize(cmd), ct);
    }

    public async Task SubscribeQuotesAsync(string symbol, CancellationToken ct = default)
    {
        symbol = NormalizeDisplaySymbol(symbol);
        var added = false;
        lock (_subscribedQuotes)
        {
            added = _subscribedQuotes.Add(symbol);
        }
        if (!added) return;
        StartSnapshotFallbackLoop();

        var needsConnect = _ws?.State != WebSocketState.Open || !_connected;
        if (needsConnect)
        {
            await ConnectAsync(ct);
            return; // Connect replayed the complete desired subscription set.
        }

        if (_ws?.State == WebSocketState.Open)
        {
            var option = IsOptionSymbol(symbol);
            await SendSubscriptionCommandAsync(option ? "LEVELONE_OPTIONS" : "LEVELONE_EQUITIES", "ADD",
                ToStreamerKey(symbol), option ? OptionFields : EquityFields, ct);
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
                    IsDelayed = sym.TryGetProperty("realtime", out var realtime) && realtime.ValueKind is JsonValueKind.True or JsonValueKind.False
                        ? !realtime.GetBoolean() : false,
                    DataSource = "REST",
                    TimestampUtc = ExtractSnapshotTimestamp(q),
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

    private static DateTime ExtractSnapshotTimestamp(JsonElement quote)
    {
        long millis = 0;
        foreach (var name in new[] { "quoteTime", "tradeTime" })
            if (quote.TryGetProperty(name, out var value) && TryReadLong(value, out var parsed))
                millis = Math.Max(millis, parsed);
        return millis > 0 ? DateTimeOffset.FromUnixTimeMilliseconds(millis).UtcDateTime : DateTime.UtcNow;
    }

    public async Task<IReadOnlyList<MarketMover>> GetMoversAsync(
        string indexSymbol, MoverSort sort, int frequency = 0, CancellationToken ct = default)
    {
        var token = await _auth.GetAccessTokenAsync(ct);
        if (string.IsNullOrEmpty(token)) throw new InvalidOperationException("Schwab market-data authentication is unavailable.");

        var sortValue = sort switch
        {
            MoverSort.Volume => "VOLUME",
            MoverSort.Trades => "TRADES",
            MoverSort.PercentChangeUp => "PERCENT_CHANGE_UP",
            MoverSort.PercentChangeDown => "PERCENT_CHANGE_DOWN",
            _ => "VOLUME"
        };
        var url = $"{_config.MarketDataApiBase}/movers/{Uri.EscapeDataString(indexSymbol)}" +
                  $"?sort={sortValue}&frequency={Math.Clamp(frequency, 0, 60)}";
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        using var response = await _http.SendAsync(request, ct);
        var body = await response.Content.ReadAsStringAsync(ct);
        if (!response.IsSuccessStatusCode)
            throw new HttpRequestException($"Schwab movers failed ({(int)response.StatusCode}): {body}");

        using var doc = JsonDocument.Parse(body);
        if (!doc.RootElement.TryGetProperty("screeners", out var screeners) || screeners.ValueKind != JsonValueKind.Array)
            return [];

        var movers = new List<MarketMover>();
        foreach (var item in screeners.EnumerateArray())
        {
            var symbol = ReadString(item, "symbol");
            if (string.IsNullOrWhiteSpace(symbol)) continue;
            movers.Add(new MarketMover
            {
                Symbol = NormalizeDisplaySymbol(symbol),
                Description = ReadString(item, "description"),
                LastPrice = ReadDecimal(item, "lastPrice"),
                NetChange = ReadDecimal(item, "netChange"),
                NetPercentChange = ReadDecimal(item, "netPercentChange"),
                PreviousClose = ReadDecimal(item, "closePrice"),
                Volume = ReadLong(item, "totalVolume", "volume"),
                Trades = ReadLong(item, "trades"),
                NewsReference = ReadFirstString(item, "newsHeadline", "headline", "news", "newsUrl")
            });
        }

        await EnrichMoversAsync(movers, token, ct);
        return movers;
    }

    private async Task EnrichMoversAsync(List<MarketMover> movers, string token, CancellationToken ct)
    {
        if (movers.Count == 0) return;
        using var request = new HttpRequestMessage(HttpMethod.Get,
            $"{_config.MarketDataApiBase}/quotes?symbols={Uri.EscapeDataString(string.Join(',', movers.Select(m => ToStreamerKey(m.Symbol))))}&fields=quote,fundamental,reference");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        using var response = await _http.SendAsync(request, ct);
        if (!response.IsSuccessStatusCode) return;
        var body = await response.Content.ReadAsStringAsync(ct);
        using var doc = JsonDocument.Parse(body);

        foreach (var mover in movers)
        {
            if (!TryGetQuoteElement(doc.RootElement, mover.Symbol, ToStreamerKey(mover.Symbol), out var root)) continue;
            if (root.TryGetProperty("quote", out var quote))
            {
                mover.LastPrice = ReadDecimal(quote, "lastPrice", mover.LastPrice);
                mover.NetChange = ReadDecimal(quote, "netChange", mover.NetChange);
                mover.NetPercentChange = ReadDecimal(quote, "netPercentChange", mover.NetPercentChange);
                mover.PreviousClose = ReadDecimal(quote, "closePrice", mover.PreviousClose);
                mover.Volume = ReadLong(quote, mover.Volume, "totalVolume");
                mover.Bid = ReadDecimal(quote, "bidPrice");
                mover.Ask = ReadDecimal(quote, "askPrice");
                mover.Week52High = ReadDecimal(quote, "52WeekHigh");
                mover.Week52Low = ReadDecimal(quote, "52WeekLow");
            }
            if (root.TryGetProperty("fundamental", out var fundamental))
                mover.Average10DayVolume = ReadLong(fundamental, 0, "avg10DaysVolume", "average10DayVolume");
            if (string.IsNullOrWhiteSpace(mover.Description) && root.TryGetProperty("reference", out var reference))
                mover.Description = ReadString(reference, "description");
            if (string.IsNullOrWhiteSpace(mover.NewsReference))
            {
                mover.NewsReference = ReadFirstString(root, "newsHeadline", "headline", "news", "newsUrl");
                if (string.IsNullOrWhiteSpace(mover.NewsReference) && root.TryGetProperty("reference", out var newsReference))
                    mover.NewsReference = ReadFirstString(newsReference, "newsHeadline", "headline", "news", "newsUrl");
            }
        }
    }

    private static string ReadString(JsonElement source, string property) =>
        source.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() ?? "" : "";

    private static string ReadFirstString(JsonElement source, params string[] properties)
    {
        foreach (var property in properties)
        {
            var value = ReadString(source, property);
            if (!string.IsNullOrWhiteSpace(value)) return value;
        }
        return "";
    }

    private static decimal ReadDecimal(JsonElement source, string property, decimal fallback = 0) =>
        source.TryGetProperty(property, out var value) && TryReadDecimal(value, out var parsed) ? parsed : fallback;

    private static long ReadLong(JsonElement source, params string[] properties) => ReadLong(source, 0, properties);

    private static long ReadLong(JsonElement source, long fallback, params string[] properties)
    {
        foreach (var property in properties)
            if (source.TryGetProperty(property, out var value) && TryReadLong(value, out var parsed)) return parsed;
        return fallback;
    }

    public async Task SubscribeDepthAsync(string symbol, CancellationToken ct = default)
    {
        symbol = NormalizeDisplaySymbol(symbol);
        var added = false;
        lock (_subscribedDepth)
        {
            added = _subscribedDepth.Add(symbol);
        }
        if (!added) return;

        var needsConnect = _ws?.State != WebSocketState.Open || !_connected;
        if (needsConnect)
        {
            await ConnectAsync(ct);
            return;
        }

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

        var equities = quoteSymbols.Where(s => !IsOptionSymbol(s)).Select(ToStreamerKey).Distinct().ToArray();
        var options = quoteSymbols.Where(IsOptionSymbol).Select(ToStreamerKey).Distinct().ToArray();
        if (equities.Length > 0)
            await SendSubscriptionCommandAsync("LEVELONE_EQUITIES", "SUBS", string.Join(',', equities), EquityFields, ct);
        if (options.Length > 0)
            await SendSubscriptionCommandAsync("LEVELONE_OPTIONS", "SUBS", string.Join(',', options), OptionFields, ct);

        foreach (var symbol in depthSymbols)
            await SendDepthSubscribeAsync(symbol, ct);
    }

    private async Task SendDepthSubscribeAsync(string symbol, CancellationToken ct)
    {
        var key = ToStreamerKey(symbol);
        if (IsOptionSymbol(symbol))
        {
            await SendSubscriptionCommandAsync("OPTIONS_BOOK", "ADD", key, "0,1,2,3", ct);
            return;
        }

        // Use both equity books; Schwab will deliver the matching venue.
        await SendSubscriptionCommandAsync("NYSE_BOOK", "ADD", key, "0,1,2,3", ct);
        await SendSubscriptionCommandAsync("NASDAQ_BOOK", "ADD", key, "0,1,2,3", ct);
    }

    private void StartSnapshotFallbackLoop()
    {
        if (_snapshotCts != null) return;
        _snapshotCts = new CancellationTokenSource();
        _ = Task.Run(() => SnapshotFallbackLoopAsync(_snapshotCts.Token));
    }

    private async Task SnapshotFallbackLoopAsync(CancellationToken ct)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(5));
        while (!ct.IsCancellationRequested && await timer.WaitForNextTickAsync(ct).ConfigureAwait(false))
        {
            if (!IsExtendedEquitySession(DateTime.UtcNow))
                continue;
            string[] symbols;
            lock (_subscribedQuotes)
            {
                symbols = _subscribedQuotes
                    .Where(s => !_connected
                                || !_lastWsQuoteUtc.TryGetValue(s, out var last)
                                || DateTime.UtcNow - last > TimeSpan.FromSeconds(8))
                    .ToArray();
            }

            if (symbols.Length == 0)
                continue;

            try
            {
                var quotes = await GetSnapshotsAsync(symbols, ct).ConfigureAwait(false);
                foreach (var quote in quotes)
                    PublishQuote(quote, source: "REST-FALLBACK", preserveTimestamp: true);

                if (_connected && _lastHeartbeatUtc != DateTime.MinValue
                               && DateTime.UtcNow - _lastHeartbeatUtc > TimeSpan.FromSeconds(45))
                {
                    _logger.LogWarning("Schwab streamer heartbeat stale; reconnecting");
                    _wsCts?.Cancel();
                }
            }
            catch (OperationCanceledException) { }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Schwab snapshot fallback loop failed");
            }
        }
    }

    internal static bool IsExtendedEquitySession(DateTime utcNow)
    {
        var zone = TimeZoneInfo.FindSystemTimeZoneById("Eastern Standard Time");
        var eastern = TimeZoneInfo.ConvertTimeFromUtc(utcNow, zone);
        if (eastern.DayOfWeek is DayOfWeek.Saturday or DayOfWeek.Sunday) return false;
        return eastern.TimeOfDay >= new TimeSpan(7, 0, 0)
               && eastern.TimeOfDay <= new TimeSpan(20, 0, 0);
    }

    public async Task UnsubscribeQuotesAsync(string symbol, CancellationToken ct = default)
    {
        symbol = NormalizeDisplaySymbol(symbol);
        lock (_subscribedQuotes)
        {
            _subscribedQuotes.Remove(symbol);
        }
        if (_connected)
        {
            var option = IsOptionSymbol(symbol);
            await SendSubscriptionCommandAsync(option ? "LEVELONE_OPTIONS" : "LEVELONE_EQUITIES", "UNSUBS",
                ToStreamerKey(symbol), option ? OptionFields : EquityFields, ct);
        }
    }

    public async Task UnsubscribeDepthAsync(string symbol, CancellationToken ct = default)
    {
        symbol = NormalizeDisplaySymbol(symbol);
        lock (_subscribedDepth)
        {
            _subscribedDepth.Remove(symbol);
        }
        if (!_connected) return;
        var key = ToStreamerKey(symbol);
        if (IsOptionSymbol(symbol))
            await SendSubscriptionCommandAsync("OPTIONS_BOOK", "UNSUBS", key, "0,1,2,3", ct);
        else
        {
            await SendSubscriptionCommandAsync("NYSE_BOOK", "UNSUBS", key, "0,1,2,3", ct);
            await SendSubscriptionCommandAsync("NASDAQ_BOOK", "UNSUBS", key, "0,1,2,3", ct);
        }
    }

    private async Task<StreamResponse> SendSubscriptionCommandAsync(
        string service, string command, string symbols, string fields, CancellationToken ct)
    {
        var requestId = Interlocked.Increment(ref _requestId);
        var cmd = new
        {
            requests = new[]
            {
                new
                {
                    service,
                    command,
                    requestid = requestId,
                    SchwabClientCustomerId = _streamerCustomerId ?? "fastdom",
                    SchwabClientCorrelId = _streamerCorrelId ?? Guid.NewGuid().ToString("N"),
                    parameters = new { keys = symbols, fields }
                }
            }
        };
        _logger.LogInformation("Schwab stream {Command} {Service} {Symbols} fields={Fields}", command, service, symbols, fields);
        var response = await SendAndAwaitResponseAsync(requestId, JsonSerializer.Serialize(cmd), ct);
        if (response.Code != 0 && response.Code is not 26 and not 27 and not 28 and not 29)
            throw new InvalidOperationException($"Schwab {service} {command} failed ({response.Code}): {response.Message}");
        if (service.EndsWith("_BOOK", StringComparison.Ordinal) && command is "SUBS" or "ADD")
            _supportsLevelTwo = true;
        return response;
    }

    private async Task SendAsync(string json, CancellationToken ct)
    {
        await _sendGate.WaitAsync(ct);
        try
        {
            if (_ws?.State != WebSocketState.Open)
                throw new WebSocketException("Schwab streamer is not open");
            var bytes = Encoding.UTF8.GetBytes(json);
            await _ws.SendAsync(bytes, WebSocketMessageType.Text, true, ct);
        }
        finally
        {
            _sendGate.Release();
        }
    }

    private async Task<StreamResponse> SendAndAwaitResponseAsync(int requestId, string json, CancellationToken ct)
    {
        var completion = new TaskCompletionSource<StreamResponse>(TaskCreationOptions.RunContinuationsAsynchronously);
        if (!_pendingRequests.TryAdd(requestId, completion))
            throw new InvalidOperationException($"Duplicate Schwab streamer request id {requestId}");
        try
        {
            await SendAsync(json, ct);
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(TimeSpan.FromSeconds(10));
            return await completion.Task.WaitAsync(timeout.Token);
        }
        finally
        {
            _pendingRequests.TryRemove(requestId, out _);
        }
    }

    private Task<StreamResponse> SubscribeAccountActivityAsync(CancellationToken ct) =>
        SendSubscriptionCommandAsync("ACCT_ACTIVITY", "SUBS", "FastDOM Account Activity", "0,1,2,3", ct);

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
        foreach (var pending in _pendingRequests.Values)
            pending.TrySetException(new WebSocketException("Schwab streamer disconnected"));
        if (!_intentionalDisconnect && _disposed == 0)
            _ = Task.Run(ReconnectLoopAsync);
    }

    private void ParseStreamMessage(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;

            if (root.TryGetProperty("notify", out var notify))
            {
                foreach (var item in notify.EnumerateArray())
                    if (item.TryGetProperty("heartbeat", out _))
                        _lastHeartbeatUtc = DateTime.UtcNow;
            }

            if (root.TryGetProperty("data", out var data))
            {
                foreach (var item in data.EnumerateArray())
                {
                    var service = item.TryGetProperty("service", out var s) ? s.GetString() : "";
                    if (service is "LEVELONE_EQUITIES" or "LEVELONE_OPTIONS")
                        ParseL1Data(item);
                    else if (service is "LISTED_BOOK" or "NYSE_BOOK" or "NASDAQ_BOOK" or "OPTIONS_BOOK")
                        ParseDepthData(item);
                    else if (service is "ACCT_ACTIVITY" or "ACCOUNT_ACTIVITY")
                        ParseAccountActivity(item);
                }
            }

            if (root.TryGetProperty("response", out var response))
            {
                foreach (var item in response.EnumerateArray())
                {
                    var service = item.TryGetProperty("service", out var s) ? s.GetString() : "";
                    var command = item.TryGetProperty("command", out var c) ? c.GetString() : "";
                    var requestId = item.TryGetProperty("requestid", out var rid) && TryReadInt(rid, out var parsedId)
                        ? parsedId : -1;
                    var code = item.TryGetProperty("content", out var content) && content.TryGetProperty("code", out var codeElement)
                        ? TryReadInt(codeElement, out var codeValue) ? codeValue : -1
                        : -1;
                    var message = item.TryGetProperty("content", out content) && content.TryGetProperty("msg", out var msgElement)
                        ? msgElement.GetString()
                        : "";
                    _logger.LogInformation("Schwab stream response {Service} {Command} code={Code} {Message}", service, command, code, message);
                    if (requestId >= 0 && _pendingRequests.TryGetValue(requestId, out var completion))
                        completion.TrySetResult(new StreamResponse(requestId, service ?? "", command ?? "", code, message ?? ""));
                    if (code is 3 or 12 or 30)
                        _wsCts?.Cancel();
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
        var service = item.TryGetProperty("service", out var serviceElement) ? serviceElement.GetString() : "";
        foreach (var c in content.EnumerateArray())
        {
            var symbol = c.TryGetProperty("key", out var k) ? NormalizeDisplaySymbol(k.GetString() ?? "") : "";
            if (string.IsNullOrWhiteSpace(symbol)) continue;

            _quotesBySymbol.TryGetValue(symbol, out var previous);
            var quote = previous != null ? CopyQuote(previous) : new Quote { Symbol = symbol };
            var hadTrade = service == "LEVELONE_OPTIONS"
                ? ApplyOptionFields(c, quote)
                : ApplyEquityFields(c, quote);
            if (c.TryGetProperty("delayed", out var delayed) && delayed.ValueKind is JsonValueKind.True or JsonValueKind.False)
                quote.IsDelayed = delayed.GetBoolean();

            // Schwab stream messages are partial. Do not publish an unusable
            // quote that would recenter the DOM on zero.
            if (quote.Last <= 0 && quote.Bid <= 0 && quote.Ask <= 0)
                continue;

            _lastWsQuoteUtc[symbol] = DateTime.UtcNow;
            PublishQuote(quote, source: "WS", preserveTimestamp: false);
            if (hadTrade)
                _tradeSubject.OnNext(new Trade
                {
                    Symbol = symbol,
                    Price = quote.Last,
                    Size = quote.LastSize,
                    TimestampUtc = quote.TimestampUtc
                });
        }
    }

    internal static bool ApplyEquityFields(JsonElement c, Quote quote)
    {
        if (TryGetDecimal(c, "1", out var bid) && bid > 0) quote.Bid = bid;
        if (TryGetDecimal(c, "2", out var ask) && ask > 0) quote.Ask = ask;
        var hadTrade = TryGetDecimal(c, "3", out var last) && last > 0;
        if (hadTrade) quote.Last = last;
        if (TryGetInt(c, "4", out var bidSize)) quote.BidSize = bidSize;
        if (TryGetInt(c, "5", out var askSize)) quote.AskSize = askSize;
        if (TryGetLong(c, "8", out var volume)) quote.Volume = volume;
        if (TryGetInt(c, "9", out var lastSize)) quote.LastSize = lastSize;
        if (TryGetDecimal(c, "10", out var high)) quote.High = high;
        if (TryGetDecimal(c, "11", out var low)) quote.Low = low;
        if (TryGetDecimal(c, "12", out var close)) quote.Close = close;
        if (TryGetDecimal(c, "17", out var open)) quote.Open = open;
        if (TryGetDecimal(c, "18", out var change)) quote.NetChange = change;
        if (TryGetDecimal(c, "42", out var changePct)) quote.NetChangePct = changePct;
        return hadTrade;
    }

    internal static bool ApplyOptionFields(JsonElement c, Quote quote)
    {
        if (TryGetDecimal(c, "2", out var bid) && bid > 0) quote.Bid = bid;
        if (TryGetDecimal(c, "3", out var ask) && ask > 0) quote.Ask = ask;
        var hadTrade = TryGetDecimal(c, "4", out var last) && last > 0;
        if (hadTrade) quote.Last = last;
        if (TryGetDecimal(c, "5", out var high)) quote.High = high;
        if (TryGetDecimal(c, "6", out var low)) quote.Low = low;
        if (TryGetDecimal(c, "7", out var close)) quote.Close = close;
        if (TryGetLong(c, "8", out var volume)) quote.Volume = volume;
        if (TryGetDecimal(c, "15", out var open)) quote.Open = open;
        if (TryGetInt(c, "16", out var bidSize)) quote.BidSize = bidSize;
        if (TryGetInt(c, "17", out var askSize)) quote.AskSize = askSize;
        if (TryGetInt(c, "18", out var lastSize)) quote.LastSize = lastSize;
        return hadTrade;
    }

    private void PublishQuote(Quote quote, string source, bool preserveTimestamp = false)
    {
        if (!preserveTimestamp || quote.TimestampUtc == default)
            quote.TimestampUtc = DateTime.UtcNow;
        quote.DataSource = source;
        _quotesBySymbol[quote.Symbol] = quote;
        _quoteSubject.OnNext(quote);
        _logger.LogDebug("Schwab quote {Source} {Symbol}: last={Last} bid={Bid} ask={Ask}", source, quote.Symbol, quote.Last, quote.Bid, quote.Ask);
    }

    private void ParseDepthData(JsonElement item)
    {
        if (!item.TryGetProperty("content", out var content)) return;
        var service = item.TryGetProperty("service", out var serviceElement) ? serviceElement.GetString() ?? "BOOK" : "BOOK";
        foreach (var c in content.EnumerateArray())
        {
            var symbol = c.TryGetProperty("key", out var k) ? NormalizeDisplaySymbol(k.GetString() ?? "") : "";
            if (string.IsNullOrWhiteSpace(symbol)) continue;

            var venueKey = $"{service}|{symbol}";
            var depth = new MarketDepth { Symbol = symbol, HasRealDepth = true };
            _depthByVenueSymbol[venueKey] = depth;

            if (c.TryGetProperty("2", out var bids))
                ReplaceBookLevels(bids, depth.Bids, isBid: true);
            if (c.TryGetProperty("3", out var asks))
                ReplaceBookLevels(asks, depth.Asks, isBid: false);

            depth.TimestampUtc = DateTime.UtcNow;
            depth.HasRealDepth = true;
            var combined = CombineVenueBooks(symbol);
            _depthBySymbol[symbol] = combined;
            if (combined.Bids.Count > 0 || combined.Asks.Count > 0)
            {
                _logger.LogDebug("Schwab depth {Symbol}: bids={BidCount} asks={AskCount}", symbol, combined.Bids.Count, combined.Asks.Count);
                _depthSubject.OnNext(CloneDepth(combined));
            }
        }
    }

    private MarketDepth CombineVenueBooks(string symbol)
    {
        var books = _depthByVenueSymbol
            .Where(x => x.Key.EndsWith($"|{symbol}", StringComparison.OrdinalIgnoreCase))
            .Select(x => x.Value)
            .ToArray();
        return new MarketDepth
        {
            Symbol = symbol,
            HasRealDepth = books.Length > 0,
            TimestampUtc = books.Length > 0 ? books.Max(x => x.TimestampUtc) : DateTime.UtcNow,
            Bids = books.SelectMany(x => x.Bids).GroupBy(x => x.Price)
                .Select(g => new DomLevel { Price = g.Key, BidSize = g.Sum(x => x.BidSize) })
                .OrderByDescending(x => x.Price).Take(50).ToList(),
            Asks = books.SelectMany(x => x.Asks).GroupBy(x => x.Price)
                .Select(g => new DomLevel { Price = g.Key, AskSize = g.Sum(x => x.AskSize) })
                .OrderBy(x => x.Price).Take(50).ToList()
        };
    }

    private void ParseAccountActivity(JsonElement item)
    {
        if (!item.TryGetProperty("content", out var content) || content.ValueKind != JsonValueKind.Array) return;
        var timestamp = item.TryGetProperty("timestamp", out var ts) && TryReadLong(ts, out var epoch)
            ? DateTimeOffset.FromUnixTimeMilliseconds(epoch).UtcDateTime
            : DateTime.UtcNow;
        foreach (var activity in content.EnumerateArray())
        {
            var account = activity.TryGetProperty("1", out var accountElement) ? accountElement.ToString() : "";
            var type = activity.TryGetProperty("2", out var typeElement) ? typeElement.ToString() : "UNKNOWN";
            var data = activity.TryGetProperty("3", out var dataElement) ? dataElement.ToString() : "";
            var sequence = activity.TryGetProperty("seq", out var seqElement) && TryReadLong(seqElement, out var seq) ? seq : 0;
            if (sequence > 0 && _lastAccountSequence.TryGetValue(account, out var prior) && sequence <= prior)
                continue;
            if (sequence > 0) _lastAccountSequence[account] = sequence;
            _accountActivitySubject.OnNext(new AccountActivity
            {
                AccountId = account,
                MessageType = type,
                MessageData = data,
                Sequence = sequence,
                TimestampUtc = timestamp
            });
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

    internal static void ReplaceBookLevels(JsonElement levels, List<DomLevel> target, bool isBid)
    {
        if (levels.ValueKind != JsonValueKind.Array) return;
        target.Clear();
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

            if (size <= 0) continue;
            target.Add(isBid
                ? new DomLevel { Price = price, BidSize = size }
                : new DomLevel { Price = price, AskSize = size });
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
        IsDelayed = q.IsDelayed,
        DataSource = q.DataSource,
        TimestampUtc = q.TimestampUtc,
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

    private static bool TryReadLong(JsonElement element, out long value)
    {
        value = 0;
        if (element.ValueKind == JsonValueKind.Number) return element.TryGetInt64(out value);
        return element.ValueKind == JsonValueKind.String && long.TryParse(element.GetString(), out value);
    }

    private static string? TryGetString(JsonElement item, string property) =>
        item.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    public async Task DisconnectAsync(CancellationToken ct = default)
    {
        if (_disposed != 0) return;
        _intentionalDisconnect = true;
        CancelAndDispose(ref _snapshotCts);
        if (_ws?.State == WebSocketState.Open)
        {
            try { await SendLogoutAsync(ct); } catch (Exception ex) { _logger.LogDebug(ex, "Schwab streamer logout failed"); }
            await _ws.CloseAsync(WebSocketCloseStatus.NormalClosure, "Disconnect", CancellationToken.None);
        }
        CancelAndDispose(ref _wsCts);
        _logger.LogWarning("Schwab streaming WebSocket disconnected. State={State}", _ws?.State);
        _connected = false;
        _connectionSubject.OnNext(false);
        foreach (var pending in _pendingRequests.Values)
            pending.TrySetException(new WebSocketException("Schwab streamer disconnected"));
    }

    private async Task SendLogoutAsync(CancellationToken ct)
    {
        var requestId = Interlocked.Increment(ref _requestId);
        var cmd = new
        {
            requests = new[]
            {
                new
                {
                    service = "ADMIN",
                    command = "LOGOUT",
                    requestid = requestId,
                    SchwabClientCustomerId = _streamerCustomerId ?? "fastdom",
                    SchwabClientCorrelId = _streamerCorrelId ?? "fastdom",
                    parameters = new { }
                }
            }
        };
        await SendAndAwaitResponseAsync(requestId, JsonSerializer.Serialize(cmd), ct);
    }

    private async Task ReconnectLoopAsync()
    {
        for (var attempt = 1; attempt <= 8 && !_intentionalDisconnect && _disposed == 0; attempt++)
        {
            try
            {
                await Task.Delay(TimeSpan.FromSeconds(Math.Min(30, attempt * 2)));
                await ConnectAsync();
                if (_connected) return;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Schwab streamer reconnect attempt {Attempt} failed", attempt);
            }
        }
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
        _accountActivitySubject.Dispose();
        _connectionSubject.Dispose();
        _connectGate.Dispose();
        _sendGate.Dispose();
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
