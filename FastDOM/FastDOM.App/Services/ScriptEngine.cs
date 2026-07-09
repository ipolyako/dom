using FastDOM.Broker.Interfaces;
using FastDOM.Core.Enums;
using FastDOM.Core.Models;
using FastDOM.MarketData.Models;
using Microsoft.Extensions.Logging;

namespace FastDOM.App.Services;

// ── Execution context ─────────────────────────────────────────────────────────

public class ScriptContext
{
    public required string         Symbol      { get; init; }
    public required string         AccountId   { get; init; }
    public required int            DefaultSize { get; init; }
    public          Quote?         Quote       { get; init; }
    public          Position?      Position    { get; init; }
    public required OrderService   Orders      { get; init; }
    public required IBrokerClient  Broker      { get; init; }
    public required Action<string> Toast       { get; init; }
    // Runtime variables — set by DIALOG or carried between commands
    public Dictionary<string, decimal>          Variables  { get; } = new(StringComparer.OrdinalIgnoreCase);
    // Show an input dialog; returns null if the user cancels (must marshal to UI thread)
    public Func<string, string, Task<decimal?>>? PromptUser { get; init; }
}

// ── Script engine ─────────────────────────────────────────────────────────────

public class ScriptEngine
{
    private readonly ILogger<ScriptEngine> _logger;

    public ScriptEngine(ILogger<ScriptEngine> logger) => _logger = logger;

    // Execute a multi-command script (commands separated by ; or newlines).
    public async Task ExecuteAsync(string script, ScriptContext ctx)
    {
        if (string.IsNullOrWhiteSpace(script)) return;

        var lines = script
            .Split(new[] { ';', '\n', '\r' }, StringSplitOptions.RemoveEmptyEntries)
            .Select(s => s.Trim())
            .Where(s => s.Length > 0 && !s.StartsWith('#'));

        decimal? lastEntryPrice = null;

        foreach (var line in lines)
        {
            try
            {
                lastEntryPrice = await RunLineAsync(line, ctx, lastEntryPrice);
            }
            catch (ScriptCancelException)
            {
                ctx.Toast("Script cancelled");
                return;
            }
            catch (ScriptException ex)
            {
                ctx.Toast($"Script error: {ex.Message}");
                _logger.LogWarning("Script error [{Line}]: {Msg}", line, ex.Message);
                return;
            }
            catch (Exception ex)
            {
                ctx.Toast($"Script failed: {ex.Message}");
                _logger.LogError(ex, "Script execution failed [{Line}]", line);
                return;
            }
        }
    }

    // Returns the entry price when line is a BUY/SELL (used by following BRACKET).
    private async Task<decimal?> RunLineAsync(string line, ScriptContext ctx, decimal? prevEntry)
    {
        var tokens = line.ToUpperInvariant()
            .Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)
            .ToList();

        if (tokens.Count == 0) return prevEntry;

        int pos = 0;
        string Take() => pos < tokens.Count ? tokens[pos++] : "";
        string Peek() => pos < tokens.Count ? tokens[pos]   : "";

        var verb = Take();

        switch (verb)
        {
            // DIALOG varname — shows input dialog, stores result in Variables[varname]
            case "DIALOG":
            {
                var varName = Take();
                if (string.IsNullOrWhiteSpace(varName))
                    throw new ScriptException("DIALOG: variable name required");
                if (ctx.PromptUser == null)
                    throw new ScriptException("DIALOG requires a UI context");

                // Format "STOP" → "Stop price:" for display
                var display = varName.Length > 1
                    ? $"{char.ToUpper(varName[0])}{varName[1..].ToLower()} price:"
                    : $"{varName} price:";

                var val = await ctx.PromptUser(varName, display);
                if (val == null) throw new ScriptCancelException();
                ctx.Variables[varName] = val.Value;
                return prevEntry;
            }

            case "BUY":
            case "SELL":
            {
                var side  = verb == "BUY" ? OrderSide.Buy : OrderSide.Sell;
                var size  = ParseSize(tokens, ref pos);
                var oType = Peek() switch
                {
                    "LMT" or "LIMIT" => (Take(), OrderType.Limit).Item2,
                    "MKT" or "MARKET" => (Take(), OrderType.Market).Item2,
                    _ => OrderType.Market
                };
                PriceExpr? limitP = oType == OrderType.Limit ? ParsePrice(tokens, ref pos) : null;
                PriceExpr? stopP  = null;
                if (Peek() == "STOP") { Take(); stopP = ParsePrice(tokens, ref pos); }

                decimal? limitVal = ResolvePrice(limitP, ctx.Quote, vars: ctx.Variables);
                decimal? stopVal  = ResolvePrice(stopP,  ctx.Quote, vars: ctx.Variables);
                int qty = ResolveSize(size, ctx.DefaultSize, ctx.Position, ctx.Quote, limitVal, stopVal);

                if (qty <= 0)
                {
                    ctx.Toast(size.Kind == SizeKind.Risk
                        ? "Risk size resolved to 0 — check quote and stop distance"
                        : "Size resolved to 0 — order not placed");
                    return prevEntry;
                }

                // Store qty so BRACKET can size targets even before fill is confirmed
                ctx.Variables["__LASTQTY__"] = qty;

                await PlaceAsync(ctx, side, qty, oType, limitVal, null);
                return limitVal ?? (side == OrderSide.Buy ? ctx.Quote?.Ask : ctx.Quote?.Bid);
            }

            case "CANCEL":
            {
                var scope = Take();
                switch (scope)
                {
                    case "BUY":
                        await ctx.Orders.CancelSideForSymbolAsync(ctx.AccountId, ctx.Symbol, OrderSide.Buy);
                        ctx.Toast($"Cancelled {ctx.Symbol} buy orders");
                        break;
                    case "SELL":
                        await ctx.Orders.CancelSideForSymbolAsync(ctx.AccountId, ctx.Symbol, OrderSide.Sell);
                        ctx.Toast($"Cancelled {ctx.Symbol} sell orders");
                        break;
                    default:
                        await ctx.Orders.CancelAllForSymbolAsync(ctx.AccountId, ctx.Symbol);
                        ctx.Toast($"Cancelled all {ctx.Symbol} orders");
                        break;
                }
                return prevEntry;
            }

            case "FLAT":
            case "FLATTEN":
            {
                var livePos = ctx.Position ?? await FetchPositionAsync(ctx);
                if (livePos == null || livePos.IsFlat) { ctx.Toast("No position to flatten"); return prevEntry; }
                await ctx.Orders.CancelAllForSymbolAsync(ctx.AccountId, ctx.Symbol);
                var flatSide = livePos.Side == PositionSide.Long ? OrderSide.Sell : OrderSide.Buy;
                await PlaceAsync(ctx, flatSide, Math.Abs(livePos.Quantity), OrderType.Market, null, null);
                return prevEntry;
            }

            case "REVERSE":
            {
                var livePos = ctx.Position ?? await FetchPositionAsync(ctx);
                if (livePos == null || livePos.IsFlat) { ctx.Toast("No position to reverse"); return prevEntry; }
                await ctx.Orders.CancelAllForSymbolAsync(ctx.AccountId, ctx.Symbol);
                var revSide = livePos.Side == PositionSide.Long ? OrderSide.Sell : OrderSide.Buy;
                await PlaceAsync(ctx, revSide, Math.Abs(livePos.Quantity) * 2, OrderType.Market, null, null);
                return prevEntry;
            }

            case "BRACKET":
            {
                await RunBracketAsync(tokens, pos, ctx, prevEntry);
                return prevEntry;
            }

            case "SECURE":
            {
                await RunSecureAsync(tokens, pos, ctx);
                return prevEntry;
            }

            default:
                throw new ScriptException($"Unknown command: {verb}");
        }
    }

    private async Task RunSecureAsync(List<string> tokens, int pos, ScriptContext ctx)
    {
        PriceExpr? stopP = null;

        while (pos < tokens.Count)
        {
            var kw = tokens[pos++];
            if (kw == "STOP")
                stopP = ParsePrice(tokens, ref pos);
        }

        if (stopP == null) { ctx.Toast("SECURE: STOP price required"); return; }

        var livePos = ctx.Position ?? await FetchPositionAsync(ctx);
        if (livePos == null || livePos.IsFlat)
        {
            ctx.Toast("SECURE: no open position");
            return;
        }

        var entry = livePos.AverageCost;
        if (entry <= 0)
        {
            ctx.Toast("SECURE: position average cost unavailable");
            return;
        }

        decimal? stopVal = ResolvePrice(stopP, ctx.Quote, entry, vars: ctx.Variables);
        if (stopVal == null) { ctx.Toast("SECURE: could not resolve STOP price"); return; }

        if (livePos.Side == PositionSide.Long && stopVal >= entry)
        {
            ctx.Toast($"SECURE: long stop {stopVal:F2} must be below entry {entry:F2}");
            return;
        }
        if (livePos.Side == PositionSide.Short && stopVal <= entry)
        {
            ctx.Toast($"SECURE: short stop {stopVal:F2} must be above entry {entry:F2}");
            return;
        }

        await ctx.Orders.CancelAllForSymbolAsync(ctx.AccountId, ctx.Symbol);

        ctx.Variables["__LASTQTY__"] = Math.Abs(livePos.Quantity);
        var targetSign = livePos.Side == PositionSide.Long ? "+" : "-";
        var bracketTokens = new List<string>
        {
            "BRACKET", "STOP", "$STOP",
            "T1", $"ENTRY{targetSign}1R", "PCT:20",
            "T2", $"ENTRY{targetSign}2R", "PCT:20",
            "T3", $"ENTRY{targetSign}3R", "PCT:20",
            "T4", $"ENTRY{targetSign}4R", "PCT:20",
            "T5", $"ENTRY{targetSign}5R", "PCT:20",
        };

        await RunBracketAsync(bracketTokens, 1, ctx, entry);
    }

    private async Task RunBracketAsync(List<string> tokens, int pos, ScriptContext ctx, decimal? entry)
    {
        PriceExpr? stopP = null;
        var targets = new List<(PriceExpr Price, SizeExpr? Size)>();

        while (pos < tokens.Count)
        {
            var kw = tokens[pos++];
            switch (kw)
            {
                case "STOP":
                    stopP = ParsePrice(tokens, ref pos);
                    break;
                case "T1": case "T2": case "T3": case "T4": case "T5":
                    var tPrice = ParsePrice(tokens, ref pos);
                    SizeExpr? tSize = null;
                    if (pos < tokens.Count && IsSizeToken(tokens[pos]))
                        tSize = ParseSize(tokens, ref pos);
                    targets.Add((tPrice, tSize));
                    break;
            }
        }

        if (stopP == null) { ctx.Toast("BRACKET: STOP price required"); return; }

        decimal? stopVal = ResolvePrice(stopP, ctx.Quote, entry, vars: ctx.Variables);
        if (stopVal == null) { ctx.Toast("BRACKET: could not resolve STOP price"); return; }

        decimal? rMultiple = entry.HasValue ? Math.Abs(entry.Value - stopVal.Value) : (decimal?)null;

        // Use __LASTQTY__ if available (set by preceding BUY/SELL before fill confirmation)
        int totalQty;
        if (ctx.Variables.TryGetValue("__LASTQTY__", out var lq) && lq > 0)
        {
            totalQty = (int)lq;
        }
        else
        {
            var livePos = ctx.Position ?? await FetchPositionAsync(ctx);
            totalQty = livePos != null && !livePos.IsFlat
                ? Math.Abs(livePos.Quantity)
                : ctx.DefaultSize;
        }

        // Exit side: if entry price is above stop → long position → sell to exit
        var exitSide = entry.HasValue
            ? (entry.Value > stopVal.Value ? OrderSide.Sell : OrderSide.Buy)
            : OrderSide.Sell;

        var targetsPlaced = 0;

        // Place target exits. When a stop is present, each target tranche is an
        // OCO: sell limit target + stop for that same tranche. This avoids
        // submitting one full-size independent stop that can consume the whole
        // position and cause Schwab to reject the sell-limit ladder.
        if (targets.Count > 0)
        {
            int remaining = totalQty;
            int perTarget = Math.Max(1, totalQty / targets.Count);

            for (int i = 0; i < targets.Count; i++)
            {
                decimal? tVal = ResolvePrice(targets[i].Price, ctx.Quote, entry, rMultiple, ctx.Variables);
                if (tVal == null) continue;

                int tQty;
                var tSize = targets[i].Size;
                if (tSize?.Kind == SizeKind.Pct)
                    tQty = (int)Math.Ceiling(totalQty * tSize.Value / 100m);
                else if (tSize != null)
                    tQty = ResolveSize(tSize, remaining, ctx.Position, ctx.Quote, entry, stopVal);
                else
                    tQty = i == targets.Count - 1 ? remaining : perTarget;

                tQty = Math.Min(tQty, remaining);
                if (tQty <= 0) break;
                remaining -= tQty;

                await PlaceAsync(ctx, exitSide, tQty,
                    stopVal.HasValue ? OrderType.OCO : OrderType.Limit,
                    tVal, stopVal);
                targetsPlaced++;
            }
        }
        else
        {
            // No targets specified: just place the protective stop.
            await PlaceAsync(ctx, exitSide, totalQty, OrderType.StopMarket, null, stopVal);
        }

        ctx.Toast(targetsPlaced > 0
            ? $"Bracket set: {targetsPlaced} OCO target{(targetsPlaced == 1 ? "" : "s")} with stop @ {stopVal:F2}"
            : $"Bracket set: stop @ {stopVal:F2}");
    }

    // A token is a size token (vs a price token) when it unambiguously denotes a quantity.
    private static bool IsSizeToken(string t) =>
        t.StartsWith("PCT:") || t.StartsWith("RISK:$") || t is "HALF" or "SIZE" or "POS";

    // ── Helpers ───────────────────────────────────────────────────────────────

    private async Task PlaceAsync(ScriptContext ctx, OrderSide side, int qty,
        OrderType orderType, decimal? limitPrice, decimal? stopPrice)
    {
        if (qty <= 0) return;
        var account = await ctx.Broker.GetAccountSummaryAsync(ctx.AccountId);
        var req = new OrderRequest
        {
            AccountId  = ctx.AccountId,
            Symbol     = ctx.Symbol,
            Side       = side,
            Quantity   = qty,
            OrderType  = orderType,
            LimitPrice = limitPrice,
            StopPrice  = stopPrice,
            Source     = OrderSource.HotButton,
        };
        var (ok, msg) = await ctx.Orders.SubmitOrderAsync(req, account, ctx.Quote);
        if (!ok) ctx.Toast($"REJECTED: {msg}");
    }

    private static async Task<Position?> FetchPositionAsync(ScriptContext ctx)
    {
        var summary = await ctx.Broker.GetAccountSummaryAsync(ctx.AccountId);
        summary.Positions.TryGetValue(ctx.Symbol, out var pos);
        return pos;
    }

    // ── Size resolution ───────────────────────────────────────────────────────

    private static int ResolveSize(SizeExpr? s, int defaultSize,
        Position? pos, Quote? quote, decimal? entryPrice, decimal? stopPrice)
    {
        if (s == null) return defaultSize;
        return s.Kind switch
        {
            SizeKind.UseDefault => defaultSize,
            SizeKind.Shares     => (int)s.Value,
            SizeKind.Dollar     =>
                entryPrice > 0    ? (int)(s.Value / entryPrice.Value)
                : quote?.Last > 0 ? (int)(s.Value / quote.Last)
                : defaultSize,
            SizeKind.Half =>
                pos is { IsFlat: false } ? Math.Abs(pos.Quantity) / 2 : defaultSize,
            SizeKind.Pct =>
                pos is { IsFlat: false }
                    ? (int)Math.Ceiling(Math.Abs(pos.Quantity) * s.Value / 100m)
                    : defaultSize,
            // For Risk sizing, fall back to current ask/bid when entry (limit) is unknown (market order)
            SizeKind.Risk =>
                stopPrice.HasValue
                    ? (int)(s.Value / Math.Max(0.01m, Math.Abs(
                        (entryPrice ?? quote?.Ask ?? quote?.Last ?? 0m) - stopPrice.Value)))
                    : defaultSize,
            _ => defaultSize
        };
    }

    // ── Price resolution ──────────────────────────────────────────────────────

    private static decimal? ResolvePrice(PriceExpr? p, Quote? q,
        decimal? entry = null, decimal? rMultiple = null,
        IReadOnlyDictionary<string, decimal>? vars = null)
    {
        if (p == null) return null;

        // Variable reference: $STOP → look up Variables["STOP"]
        if (p is VarExpr ve)
            return vars?.TryGetValue(ve.VarName, out var vv) == true ? vv + p.Offset : null;

        // R-multiple: ENTRY+2R
        if (p is RExpr re)
            return entry == null ? null : entry + re.RCount * (rMultiple ?? 0m);

        decimal? @base = p.Ref switch
        {
            PriceRef.Ask     => q?.Ask,
            PriceRef.Bid     => q?.Bid,
            PriceRef.Last    => q?.Last,
            PriceRef.Mid     => q?.Mid,
            PriceRef.Entry   => entry,
            PriceRef.Literal => p.Offset,
            _                => null
        };

        return p.Ref == PriceRef.Literal ? @base : @base + p.Offset;
    }

    // ── Parser ────────────────────────────────────────────────────────────────

    private static SizeExpr ParseSize(List<string> t, ref int pos)
    {
        if (pos >= t.Count) return new SizeExpr(SizeKind.UseDefault, 0);
        var tok = t[pos];

        if (tok.StartsWith("RISK:$") && decimal.TryParse(tok[6..], out var rv))
            return (pos++, new SizeExpr(SizeKind.Risk, rv)).Item2;

        if (tok.StartsWith("PCT:") && decimal.TryParse(tok[4..], out var pv))
            return (pos++, new SizeExpr(SizeKind.Pct, pv)).Item2;

        if (tok.StartsWith('$') && decimal.TryParse(tok[1..], out var dv))
            return (pos++, new SizeExpr(SizeKind.Dollar, dv)).Item2;

        return tok switch
        {
            "SIZE" or "SSHARE" => (pos++, new SizeExpr(SizeKind.UseDefault, 0)).Item2,
            "HALF"             => (pos++, new SizeExpr(SizeKind.Half, 0)).Item2,
            "POS"              => (pos++, new SizeExpr(SizeKind.Pct, 100)).Item2,
            _ when decimal.TryParse(tok, out var n)
                               => (pos++, new SizeExpr(SizeKind.Shares, n)).Item2,
            _                  => new SizeExpr(SizeKind.UseDefault, 0)
        };
    }

    // Parses: ENTRY+1R | ASK+0.05 | BID-0.10 | LAST | 550.00 | $STOP
    private static PriceExpr ParsePrice(List<string> t, ref int pos)
    {
        if (pos >= t.Count) throw new ScriptException("Expected price expression");
        var tok = t[pos++];

        // Variable reference: $STOP (or $STOP+0.05)
        if (tok.StartsWith('$') && !decimal.TryParse(tok[1..], out _))
        {
            var varName = tok[1..];
            // Check for offset: $STOP+0.05 would be one token after tokenization — handle if present
            for (int j = 1; j < varName.Length; j++)
            {
                if (varName[j] is not ('+' or '-')) continue;
                var vname = varName[..j];
                var sign  = varName[j] == '+' ? 1m : -1m;
                if (decimal.TryParse(varName[(j + 1)..], out var voff))
                    return new VarExpr(vname, sign * voff);
                break;
            }
            return new VarExpr(varName, 0);
        }

        // Compound token with +/- (skip index 0 for negative literals)
        for (int i = 1; i < tok.Length; i++)
        {
            if (tok[i] is not ('+' or '-')) continue;
            var left  = tok[..i];
            var sign  = tok[i] == '+' ? 1m : -1m;
            var right = tok[(i + 1)..];

            // R-multiple: ENTRY+1R
            if (right.EndsWith('R') && decimal.TryParse(right[..^1], out var rm))
                return new RExpr(ParseRef(left), sign * rm);

            if (decimal.TryParse(right, out var off))
                return new PriceExpr(ParseRef(left), sign * off);

            break;
        }

        // Literal number
        if (decimal.TryParse(tok, out var lit))
            return new PriceExpr(PriceRef.Literal, lit);

        return new PriceExpr(ParseRef(tok), 0);
    }

    private static PriceRef ParseRef(string s) => s switch
    {
        "ASK"   => PriceRef.Ask,
        "BID"   => PriceRef.Bid,
        "LAST"  => PriceRef.Last,
        "MID"   => PriceRef.Mid,
        "ENTRY" => PriceRef.Entry,
        _       => PriceRef.Ask
    };
}

// ── Value types ───────────────────────────────────────────────────────────────

internal enum SizeKind { UseDefault, Shares, Dollar, Half, Pct, Risk }
internal enum PriceRef  { Ask, Bid, Last, Mid, Entry, Literal }

internal class SizeExpr(SizeKind kind, decimal value)
{
    public SizeKind Kind  { get; } = kind;
    public decimal  Value { get; } = value;
}

internal class PriceExpr(PriceRef @ref, decimal offset)
{
    public PriceRef Ref    { get; } = @ref;
    public decimal  Offset { get; } = offset;
}

// R-multiple: entry ± N*R  (e.g. ENTRY+2R)
internal sealed class RExpr : PriceExpr
{
    public decimal RCount { get; }
    public RExpr(PriceRef @ref, decimal rCount) : base(@ref, 0) => RCount = rCount;
}

// Variable reference: $STOP (resolves via ScriptContext.Variables at runtime)
internal sealed class VarExpr : PriceExpr
{
    public string VarName { get; }
    public VarExpr(string varName, decimal offset = 0) : base(PriceRef.Literal, offset) => VarName = varName;
}

public class ScriptException : Exception
{
    public ScriptException(string message) : base(message) { }
}

public class ScriptCancelException : Exception
{
    public ScriptCancelException() : base("Cancelled") { }
}
