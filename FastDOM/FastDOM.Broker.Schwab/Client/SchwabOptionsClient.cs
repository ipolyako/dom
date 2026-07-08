using System.Text.Json;
using FastDOM.Broker.Schwab.Auth;
using FastDOM.Infrastructure.Config;
using FastDOM.MarketData.Interfaces;
using FastDOM.MarketData.Models;
using Microsoft.Extensions.Logging;

namespace FastDOM.Broker.Schwab.Client;

/// <summary>
/// Fetches options chain data from Schwab Market Data API.
///
/// Primary endpoint:
///   GET /marketdata/v1/chains?symbol={symbol}&contractType=ALL&strikeCount=200&strategy=SINGLE
///
/// Response uses putExpDateMap/callExpDateMap keyed by expiration (yyyy-MM-dd:daysToExp) -> strike.
/// </summary>
public class SchwabOptionsClient : IOptionsDataProvider
{
    private readonly ILogger<SchwabOptionsClient> _logger;
    private readonly SchwabConfig _config;
    private readonly SchwabAuthProvider _auth;
    private readonly HttpClient _http;

    public SchwabOptionsClient(
        ILogger<SchwabOptionsClient> logger,
        SchwabConfig config,
        SchwabAuthProvider auth)
    {
        _logger = logger;
        _config = config;
        _auth = auth;
        _http = new HttpClient();
        _http.DefaultRequestHeaders.Add("Accept", "application/json");
    }

    public async Task<IReadOnlyList<DateOnly>> GetExpirationDatesAsync(
        string underlying, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(underlying)) return [];

        try
        {
            var root = await FetchChainAsync(underlying, ct: ct);
            if (!root.HasValue) return [];

            var dates = new SortedSet<DateOnly>();

            if (root.Value.TryGetProperty("callExpDateMap", out var callMap) &&
                callMap.ValueKind == JsonValueKind.Object)
            {
                AddExpirations(callMap, dates);
            }

            if (root.Value.TryGetProperty("putExpDateMap", out var putMap) &&
                putMap.ValueKind == JsonValueKind.Object)
            {
                AddExpirations(putMap, dates);
            }

            return dates.ToList();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "GetExpirationDatesAsync failed for {Underlying}", underlying);
            return [];
        }
    }

    public async Task<IReadOnlyList<OptionsChainRow>> GetChainAsync(
        string underlying, DateOnly expiration, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(underlying)) return [];

        try
        {
            // Narrow request to one expiration window when supported by Schwab.
            var root = await FetchChainAsync(
                underlying,
                from: expiration,
                to: expiration,
                ct: ct);

            if (!root.HasValue) return [];

            var calls = new Dictionary<decimal, ChainSideRow>(DecimalComparer.Instance);
            var puts = new Dictionary<decimal, ChainSideRow>(DecimalComparer.Instance);

            if (root.Value.TryGetProperty("callExpDateMap", out var callMap) &&
                callMap.ValueKind == JsonValueKind.Object)
            {
                AddContractsForExpiration(callMap, expiration, calls);
            }

            if (root.Value.TryGetProperty("putExpDateMap", out var putMap) &&
                putMap.ValueKind == JsonValueKind.Object)
            {
                AddContractsForExpiration(putMap, expiration, puts);
            }

            var strikes = calls.Keys.Union(puts.Keys).OrderBy(s => s).ToList();
            return strikes.Select(s =>
            {
                calls.TryGetValue(s, out var call);
                puts.TryGetValue(s, out var put);

                return new OptionsChainRow
                {
                    Strike     = s,
                    CallSymbol = call?.Symbol ?? "",
                    PutSymbol  = put?.Symbol ?? "",
                    CallBid    = call?.Bid,
                    CallAsk    = call?.Ask,
                    CallLast   = call?.Last,
                    CallOI     = call?.OpenInterest,
                    CallVolume = call?.Volume,
                    CallIV     = call?.ImpliedVolatility,
                    CallDelta  = call?.Delta,
                    CallTheta  = call?.Theta,
                    PutBid     = put?.Bid,
                    PutAsk     = put?.Ask,
                    PutLast    = put?.Last,
                    PutOI      = put?.OpenInterest,
                    PutVolume  = put?.Volume,
                    PutIV      = put?.ImpliedVolatility,
                    PutDelta   = put?.Delta,
                    PutTheta   = put?.Theta
                };
            }).ToList();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "GetChainAsync failed for {Underlying} {Expiration}", underlying, expiration);
            return [];
        }
    }

    private void AddExpirations(JsonElement dateMap, SortedSet<DateOnly> dates)
    {
        foreach (var dateNode in dateMap.EnumerateObject())
        {
            if (TryParseExpDate(dateNode.Name, out var dt))
                dates.Add(dt);
        }
    }

    private void AddContractsForExpiration(
        JsonElement dateMap,
        DateOnly targetExp,
        Dictionary<decimal, ChainSideRow> bag)
    {
        foreach (var dateNode in dateMap.EnumerateObject())
        {
            if (!TryParseExpDate(dateNode.Name, out var exp) || exp != targetExp)
                continue;

            if (dateNode.Value.ValueKind != JsonValueKind.Object)
                continue;

            foreach (var strikeNode in dateNode.Value.EnumerateObject())
            {
                if (strikeNode.Value.ValueKind != JsonValueKind.Array)
                    continue;

                foreach (var contractNode in strikeNode.Value.EnumerateArray())
                {
                    var row = ParseContract(contractNode);
                    if (row is null)
                        continue;

                    // Keep only one contract entry per strike/type for this expiration.
                    bag[row.Strike] = row;
                }
            }
        }
    }

    private static ChainSideRow? ParseContract(JsonElement c)
    {
        var symbol = TryGetString(c, "symbol");
        var strike = TryGetDecimal(c, "strikePrice") ?? TryGetDecimal(c, "strike") ?? 0;

        if (string.IsNullOrWhiteSpace(symbol) || strike <= 0)
            return null;

        var bid = TryGetDecimal(c, "bid");
        var ask = TryGetDecimal(c, "ask");
        var last = TryGetDecimal(c, "last");
        var oi = TryGetInt(c, "openInterest");
        var vol = TryGetInt(c, "totalVolume");
        var iv = TryGetDecimal(c, "impliedVolatility");
        var theta = GetNestedDecimal(c, "greeks", "theta");
        if (theta is null)
            theta = TryGetDecimal(c, "theta");

        // Usually inside greeks node
        var delta = GetNestedDecimal(c, "greeks", "delta");
        if (delta is null)
            delta = TryGetDecimal(c, "delta");

        return new ChainSideRow
        {
            Symbol = symbol!,
            Strike = strike,
            Bid = bid,
            Ask = ask,
            Last = last,
            OpenInterest = oi,
            Volume = vol,
            ImpliedVolatility = iv,
            Delta = delta,
            Theta = theta,
        };
    }

    private static decimal? GetNestedDecimal(JsonElement root, string parent, string child)
    {
        if (!root.TryGetProperty(parent, out var p) || p.ValueKind != JsonValueKind.Object)
            return null;

        return TryGetDecimal(p, child);
    }

    private static string? TryGetString(JsonElement obj, string property)
    {
        if (!obj.TryGetProperty(property, out var p) || p.ValueKind == JsonValueKind.Null)
            return null;

        return p.ValueKind switch
        {
            JsonValueKind.String => p.GetString(),
            _ => p.ToString()
        };
    }

    private static decimal? TryGetDecimal(JsonElement obj, string property)
    {
        if (!obj.TryGetProperty(property, out var p)) return null;
        return TryGetDecimal(p);
    }

    private static decimal? TryGetDecimal(JsonElement p)
    {
        return p.ValueKind switch
        {
            JsonValueKind.Number when p.TryGetDecimal(out var n) => n,
            JsonValueKind.String when decimal.TryParse(p.GetString(), out var s) => s,
            _ => null
        };
    }

    private static int? TryGetInt(JsonElement obj, string property)
    {
        if (!obj.TryGetProperty(property, out var p)) return null;
        return TryGetInt(p);
    }

    private static int? TryGetInt(JsonElement p)
    {
        return p.ValueKind switch
        {
            JsonValueKind.Number when p.TryGetInt32(out var i) => i,
            JsonValueKind.String when int.TryParse(p.GetString(), out var s) => s,
            _ => null
        };
    }

    private static bool TryParseExpDate(string key, out DateOnly date)
    {
        date = default;
        var first = key.Split(':', 2)[0];
        return DateOnly.TryParse(first, out date);
    }

    private async Task<JsonElement?> FetchChainAsync(
        string underlying,
        DateOnly? from = null,
        DateOnly? to = null,
        CancellationToken ct = default)
    {
        var token = await _auth.GetAccessTokenAsync(ct);
        if (string.IsNullOrWhiteSpace(token))
            return null;

        var q = new Dictionary<string, string>
        {
            ["symbol"] = underlying.Trim().ToUpperInvariant(),
            ["contractType"] = "ALL",
            ["strikeCount"] = "200",
            ["strategy"] = "SINGLE",
            ["includeQuotes"] = "TRUE",
            ["includeUnderlyingQuote"] = "TRUE"
        };

        if (from.HasValue)
            q["fromDate"] = from.Value.ToString("yyyy-MM-dd");
        if (to.HasValue)
            q["toDate"] = to.Value.ToString("yyyy-MM-dd");

        using var req = new HttpRequestMessage(HttpMethod.Get, $"{_config.MarketDataApiBase}/chains?{BuildQuery(q)}");
        req.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", token);

        try
        {
            var resp = await _http.SendAsync(req, ct);
            if (!resp.IsSuccessStatusCode)
            {
                _logger.LogWarning("Schwab chains request failed: {Status}", resp.StatusCode);
                return null;
            }

            var body = await resp.Content.ReadAsStringAsync(ct);
            using var doc = JsonDocument.Parse(body);
            return doc.RootElement.Clone();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Schwab chains request exception");
            return null;
        }
    }

    private static string BuildQuery(IReadOnlyDictionary<string, string> values)
    {
        return string.Join('&', values.Select(kv => $"{Uri.EscapeDataString(kv.Key)}={Uri.EscapeDataString(kv.Value)}"));
    }

    private class ChainSideRow
    {
        public required string Symbol { get; init; }
        public decimal Strike { get; init; }
        public decimal? Bid { get; init; }
        public decimal? Ask { get; init; }
        public decimal? Last { get; init; }
        public decimal? ImpliedVolatility { get; init; }
        public decimal? Delta { get; init; }
        public decimal? Theta { get; init; }
        public int? OpenInterest { get; init; }
        public int? Volume { get; init; }
    }

    private sealed class DecimalComparer : IEqualityComparer<decimal>
    {
        public static readonly DecimalComparer Instance = new();
        public bool Equals(decimal x, decimal y) => x == y;
        public int GetHashCode(decimal obj) => obj.GetHashCode();
    }
}
