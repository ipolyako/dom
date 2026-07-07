using System.Text.Json;
using FastDOM.Infrastructure.Config;
using FastDOM.MarketData.Interfaces;
using FastDOM.MarketData.Models;
using Microsoft.Extensions.Logging;

namespace FastDOM.Broker.Alpaca.Client;

/// <summary>
/// Fetches options chain data from Alpaca.
/// Broker API  → contracts list (strikes, OI, expiry)
/// Data API v1beta1 → snapshots (bid/ask, greeks, IV)
/// </summary>
public class AlpacaOptionsClient : IOptionsDataProvider
{
    private readonly ILogger<AlpacaOptionsClient> _logger;
    private readonly AlpacaConfig _config;
    private readonly HttpClient _http;

    public AlpacaOptionsClient(ILogger<AlpacaOptionsClient> logger, AlpacaConfig config)
    {
        _logger = logger;
        _config = config;
        _http = new HttpClient();
        _http.DefaultRequestHeaders.Add("APCA-API-KEY-ID",     config.ApiKey);
        _http.DefaultRequestHeaders.Add("APCA-API-SECRET-KEY", config.ApiSecret);
    }

    // ── Expiration dates ─────────────────────────────────────────────────────

    public async Task<IReadOnlyList<DateOnly>> GetExpirationDatesAsync(
        string underlying, CancellationToken ct = default)
    {
        if (string.IsNullOrEmpty(_config.ApiKey)) return [];
        try
        {
            var today = DateOnly.FromDateTime(DateTime.Today).ToString("yyyy-MM-dd");
            var url   = $"{_config.OptionsBrokerBase}/options/contracts" +
                        $"?underlying_symbols={Uri.EscapeDataString(underlying)}" +
                        $"&status=active&expiration_date_gte={today}&limit=10000";

            var dates = new SortedSet<DateOnly>();
            string? pageToken = null;

            do
            {
                var paged = pageToken != null ? url + $"&page_token={pageToken}" : url;
                var resp  = await _http.GetAsync(paged, ct);
                if (!resp.IsSuccessStatusCode) break;

                using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync(ct));
                var root = doc.RootElement;

                if (root.TryGetProperty("option_contracts", out var arr))
                    foreach (var c in arr.EnumerateArray())
                        if (c.TryGetProperty("expiration_date", out var e) &&
                            DateOnly.TryParse(e.GetString(), out var d))
                            dates.Add(d);

                pageToken = root.TryGetProperty("next_page_token", out var nt) && nt.ValueKind != JsonValueKind.Null
                    ? nt.GetString() : null;

            } while (pageToken != null);

            return dates.ToList();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "GetExpirationDatesAsync failed for {Sym}", underlying);
            return [];
        }
    }

    // ── Full chain for one expiration ─────────────────────────────────────────

    public async Task<IReadOnlyList<OptionsChainRow>> GetChainAsync(
        string underlying, DateOnly expiration, CancellationToken ct = default)
    {
        if (string.IsNullOrEmpty(_config.ApiKey)) return [];
        try
        {
            var expStr = expiration.ToString("yyyy-MM-dd");

            // Fetch contracts (OI, symbols) and snapshots (bid/ask/greeks) in parallel
            var contractsTask  = FetchContractsAsync(underlying, expStr, ct);
            var snapshotsTask  = FetchSnapshotsAsync(underlying, expStr, ct);
            await Task.WhenAll(contractsTask, snapshotsTask);

            var contracts  = contractsTask.Result;
            var snapshots  = snapshotsTask.Result;

            // Group by strike: one call + one put per row
            var callsByStrike = contracts.Where(c => c.Type == OptionType.Call)
                                         .ToDictionary(c => c.Strike);
            var putsByStrike  = contracts.Where(c => c.Type == OptionType.Put)
                                         .ToDictionary(c => c.Strike);

            var allStrikes = callsByStrike.Keys.Union(putsByStrike.Keys).OrderBy(s => s).ToList();

            return allStrikes.Select(strike =>
            {
                callsByStrike.TryGetValue(strike, out var call);
                putsByStrike.TryGetValue(strike, out var put);
                snapshots.TryGetValue(call?.Symbol ?? "", out var callSnap);
                snapshots.TryGetValue(put?.Symbol  ?? "", out var putSnap);

                return new OptionsChainRow
                {
                    Strike     = strike,
                    CallSymbol = call?.Symbol ?? "",
                    PutSymbol  = put?.Symbol  ?? "",
                    CallBid    = callSnap?.Bid,
                    CallAsk    = callSnap?.Ask,
                    CallLast   = callSnap?.Last,
                    CallIV     = callSnap?.IV,
                    CallDelta  = callSnap?.Delta,
                    CallTheta  = callSnap?.Theta,
                    CallOI     = call?.OpenInterest,
                    CallVolume = callSnap?.Volume,
                    PutBid     = putSnap?.Bid,
                    PutAsk     = putSnap?.Ask,
                    PutLast    = putSnap?.Last,
                    PutIV      = putSnap?.IV,
                    PutDelta   = putSnap?.Delta,
                    PutTheta   = putSnap?.Theta,
                    PutOI      = put?.OpenInterest,
                    PutVolume  = putSnap?.Volume,
                };
            }).ToList();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "GetChainAsync failed for {Sym} {Exp}", underlying, expiration);
            return [];
        }
    }

    // ── Private helpers ───────────────────────────────────────────────────────

    private record ContractInfo(string Symbol, OptionType Type, decimal Strike, int OpenInterest);
    private record SnapInfo(decimal? Bid, decimal? Ask, decimal? Last, decimal? IV,
                            decimal? Delta, decimal? Theta, int? Volume);

    private async Task<List<ContractInfo>> FetchContractsAsync(
        string underlying, string expDate, CancellationToken ct)
    {
        var result    = new List<ContractInfo>();
        var url       = $"{_config.OptionsBrokerBase}/options/contracts" +
                        $"?underlying_symbols={Uri.EscapeDataString(underlying)}" +
                        $"&expiration_date={expDate}&status=active&limit=10000";
        string? token = null;

        do
        {
            var resp = await _http.GetAsync(token != null ? url + $"&page_token={token}" : url, ct);
            if (!resp.IsSuccessStatusCode) break;

            using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync(ct));
            var root = doc.RootElement;

            if (root.TryGetProperty("option_contracts", out var arr))
                foreach (var c in arr.EnumerateArray())
                {
                    var sym = Str(c, "symbol");
                    var typeStr = Str(c, "type").ToLower();
                    if (string.IsNullOrEmpty(sym)) continue;
                    var type = typeStr == "put" ? OptionType.Put : OptionType.Call;
                    var strike = Dec(c, "strike_price") ?? 0;
                    var oi     = IntStr(c, "open_interest");
                    result.Add(new ContractInfo(sym, type, strike, oi));
                }

            token = root.TryGetProperty("next_page_token", out var nt) && nt.ValueKind != JsonValueKind.Null
                ? nt.GetString() : null;
        } while (token != null);

        return result;
    }

    private async Task<Dictionary<string, SnapInfo>> FetchSnapshotsAsync(
        string underlying, string expDate, CancellationToken ct)
    {
        var result    = new Dictionary<string, SnapInfo>(StringComparer.OrdinalIgnoreCase);
        var url       = $"{_config.OptionsDataBase}/options/snapshots/{Uri.EscapeDataString(underlying)}" +
                        $"?expiration_date={expDate}&feed=indicative&limit=1000";
        string? token = null;

        do
        {
            var resp = await _http.GetAsync(token != null ? url + $"&page_token={token}" : url, ct);
            if (!resp.IsSuccessStatusCode) break;

            using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync(ct));
            var root = doc.RootElement;

            // Response is either {"snapshots":{...}} or root directly is the dict
            var snapsNode = root.TryGetProperty("snapshots", out var sn) ? sn : root;

            foreach (var entry in snapsNode.EnumerateObject())
            {
                var v     = entry.Value;
                var q     = v.TryGetProperty("latestQuote", out var lq) ? lq : default;
                var t     = v.TryGetProperty("latestTrade", out var lt) ? lt : default;
                var g     = v.TryGetProperty("greeks",      out var gr) ? gr : default;
                var iv    = v.TryGetProperty("impliedVolatility", out var ivEl) ? (decimal?)ivEl.GetDouble() : null;

                decimal? bid  = q.ValueKind == JsonValueKind.Object ? DecP(q, "bp") : null;
                decimal? ask  = q.ValueKind == JsonValueKind.Object ? DecP(q, "ap") : null;
                decimal? last = t.ValueKind == JsonValueKind.Object ? DecP(t, "p")  : null;
                decimal? delta = g.ValueKind == JsonValueKind.Object ? DecP(g, "delta") : null;
                decimal? theta = g.ValueKind == JsonValueKind.Object ? DecP(g, "theta") : null;

                var volEl = q.ValueKind == JsonValueKind.Object && q.TryGetProperty("as", out var asEl)
                    ? (int?)asEl.GetInt32() : null;

                result[entry.Name] = new SnapInfo(bid, ask, last, iv, delta, theta, null);
            }

            token = root.TryGetProperty("next_page_token", out var nt) && nt.ValueKind != JsonValueKind.Null
                ? nt.GetString() : null;
        } while (token != null);

        return result;
    }

    private static string Str(JsonElement e, string key)
        => e.TryGetProperty(key, out var v) ? v.GetString() ?? "" : "";

    private static decimal? Dec(JsonElement e, string key)
    {
        if (!e.TryGetProperty(key, out var v)) return null;
        if (v.ValueKind == JsonValueKind.Number) return (decimal)v.GetDouble();
        if (v.ValueKind == JsonValueKind.String && decimal.TryParse(v.GetString(), out var d)) return d;
        return null;
    }

    private static decimal? DecP(JsonElement e, string key)
        => e.TryGetProperty(key, out var v) && v.ValueKind == JsonValueKind.Number
            ? (decimal)v.GetDouble() : null;

    private static int IntStr(JsonElement e, string key)
    {
        if (!e.TryGetProperty(key, out var v)) return 0;
        if (v.ValueKind == JsonValueKind.Number) return v.GetInt32();
        if (v.ValueKind == JsonValueKind.String && int.TryParse(v.GetString(), out var n)) return n;
        return 0;
    }
}
