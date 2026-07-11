using System.Text.Json;
using FastDOM.Core.Enums;
using FastDOM.Core.Models;
using Microsoft.Extensions.Logging;

namespace FastDOM.Broker.Schwab.Mapping;

/// <summary>
/// Maps FastDOM internal OrderRequest to the Schwab Trader API JSON schema.
///
/// Confirmed from official Schwab API docs and schwab-py reference:
///   POST /trader/v1/accounts/{accountHash}/orders
///   Returns HTTP 201; Order ID is in the Location response header.
///
/// orderType:         MARKET | LIMIT | STOP | STOP_LIMIT | TRAILING_STOP
/// orderStrategyType: SINGLE | OCO | TRIGGER
/// duration:          DAY | GOOD_TILL_CANCEL | FILL_OR_KILL | IMMEDIATE_OR_CANCEL
/// session:           NORMAL | AM | PM | SEAMLESS
/// instruction:       BUY | SELL | SELL_SHORT | BUY_TO_COVER
/// assetType:         EQUITY | OPTION | MUTUAL_FUND | FIXED_INCOME | INDEX
/// </summary>
public class SchwabOrderMapper
{
    private readonly ILogger<SchwabOrderMapper> _logger;

    public SchwabOrderMapper(ILogger<SchwabOrderMapper> logger)
    {
        _logger = logger;
    }

    public JsonElement MapToSchwab(OrderRequest req)
    {
        return req.OrderType switch
        {
            OrderType.Market          => BuildSingle(req, "MARKET"),
            OrderType.Limit           => BuildSingle(req, "LIMIT"),
            OrderType.MarketableLimit => BuildMarketableLimit(req),
            OrderType.StopMarket      => BuildStop(req),
            OrderType.StopLimit       => BuildStopLimit(req),
            OrderType.Bracket         => BuildBracket(req),
            OrderType.OCO             => BuildOco(req),
            _ => throw new NotSupportedException(
                $"Order type {req.OrderType} is not supported by the Schwab API mapper. " +
                $"Do not submit a partial/incorrect order.")
        };
    }

    public string MapToJson(OrderRequest req) =>
        JsonSerializer.Serialize(MapToSchwab(req), new JsonSerializerOptions { WriteIndented = false });

    private JsonElement BuildSingle(OrderRequest req, string orderType)
    {
        var obj = new Dictionary<string, object>
        {
            ["orderType"]         = orderType,
            ["session"]           = MapSession(req.Session),
            ["duration"]          = MapTif(req.TimeInForce),
            ["orderStrategyType"] = "SINGLE",
            ["orderLegCollection"] = new[]
            {
                BuildLeg(req)
            }
        };

        if (req.LimitPrice.HasValue)
            obj["price"] = req.LimitPrice.Value.ToString("F2");

        if (req.StopPrice.HasValue)
            obj["stopPrice"] = req.StopPrice.Value.ToString("F2");

        return ToElement(obj);
    }

    /// <summary>
    /// Marketable limit = LIMIT order priced through the market.
    /// For a BUY: limit = ask + small buffer. For SELL: limit = bid - buffer.
    /// This gives price improvement possibility while ensuring fill like a market order.
    /// </summary>
    private JsonElement BuildMarketableLimit(OrderRequest req)
    {
        if (!req.LimitPrice.HasValue)
            throw new ArgumentException("MarketableLimit requires a LimitPrice");

        var obj = new Dictionary<string, object>
        {
            ["orderType"]         = "LIMIT",
            ["session"]           = MapSession(req.Session),
            ["duration"]          = "DAY",
            ["orderStrategyType"] = "SINGLE",
            ["price"]             = req.LimitPrice.Value.ToString("F2"),
            ["orderLegCollection"] = new[] { BuildLeg(req) }
        };
        return ToElement(obj);
    }

    private JsonElement BuildStop(OrderRequest req)
    {
        if (!req.StopPrice.HasValue)
            throw new ArgumentException("Stop market order requires StopPrice");

        var obj = new Dictionary<string, object>
        {
            ["orderType"]         = "STOP",
            ["session"]           = MapSession(req.Session),
            ["duration"]          = MapTif(req.TimeInForce),
            ["orderStrategyType"] = "SINGLE",
            ["stopPrice"]         = req.StopPrice.Value.ToString("F2"),
            ["orderLegCollection"] = new[] { BuildLeg(req) }
        };
        return ToElement(obj);
    }

    private JsonElement BuildStopLimit(OrderRequest req)
    {
        if (!req.StopPrice.HasValue || !req.LimitPrice.HasValue)
            throw new ArgumentException("Stop limit requires both StopPrice and LimitPrice");

        var obj = new Dictionary<string, object>
        {
            ["orderType"]         = "STOP_LIMIT",
            ["session"]           = MapSession(req.Session),
            ["duration"]          = MapTif(req.TimeInForce),
            ["orderStrategyType"] = "SINGLE",
            ["stopPrice"]         = req.StopPrice.Value.ToString("F2"),
            ["price"]             = req.LimitPrice.Value.ToString("F2"),
            ["orderLegCollection"] = new[] { BuildLeg(req) }
        };
        return ToElement(obj);
    }

    /// <summary>
    /// Bracket = TRIGGER (entry) wrapping OCO (profit target + stop loss).
    /// Confirmed schema from official Schwab API docs and schwab-py.
    /// </summary>
    private JsonElement BuildBracket(OrderRequest req)
    {
        if (req.Bracket == null)
            throw new ArgumentException("Bracket order requires BracketConfig");

        var exitInstruction = req.Side == OrderSide.Buy ? "SELL" : "BUY_TO_COVER";
        var symbol = MapSymbol(req.Symbol, req.AssetType);
        var qty = req.Quantity;
        var assetType = MapAssetType(req.AssetType);

        var profitLeg = new object[]
        {
            new Dictionary<string, object>
            {
                ["instruction"] = exitInstruction,
                ["quantity"]    = qty,
                ["instrument"]  = new Dictionary<string, string> { ["symbol"] = symbol, ["assetType"] = assetType }
            }
        };

        var stopLeg = new object[]
        {
            new Dictionary<string, object>
            {
                ["instruction"] = exitInstruction,
                ["quantity"]    = qty,
                ["instrument"]  = new Dictionary<string, string> { ["symbol"] = symbol, ["assetType"] = assetType }
            }
        };

        var profitTarget = new Dictionary<string, object>
        {
            ["orderType"]         = "LIMIT",
            ["session"]           = "NORMAL",
            ["duration"]          = "GOOD_TILL_CANCEL",
            ["orderStrategyType"] = "SINGLE",
            ["price"]             = (req.Bracket.ProfitTargetPrice ?? 0).ToString("F2"),
            ["orderLegCollection"] = profitLeg
        };

        var stopLoss = new Dictionary<string, object>
        {
            ["orderType"]         = "STOP_LIMIT",
            ["session"]           = "NORMAL",
            ["duration"]          = "GOOD_TILL_CANCEL",
            ["orderStrategyType"] = "SINGLE",
            ["stopPrice"]         = (req.Bracket.StopLossPrice ?? 0).ToString("F2"),
            ["price"]             = ((req.Bracket.StopLossPrice ?? 0) - 0.01m).ToString("F2"),
            ["orderLegCollection"] = stopLeg
        };

        var oco = new Dictionary<string, object>
        {
            ["orderStrategyType"]  = "OCO",
            ["childOrderStrategies"] = new[] { profitTarget, stopLoss }
        };

        var entry = new Dictionary<string, object>
        {
            ["orderType"]         = req.LimitPrice.HasValue ? "LIMIT" : "MARKET",
            ["session"]           = MapSession(req.Session),
            ["duration"]          = MapTif(req.TimeInForce),
            ["orderStrategyType"] = "TRIGGER",
            ["orderLegCollection"] = new[] { BuildLeg(req) },
            ["childOrderStrategies"] = new[] { oco }
        };

        if (req.LimitPrice.HasValue)
            entry["price"] = req.LimitPrice.Value.ToString("F2");

        return ToElement(entry);
    }

    private JsonElement BuildOco(OrderRequest req)
    {
        if (!req.LimitPrice.HasValue || !req.StopPrice.HasValue)
            throw new ArgumentException("OCO exit requires both LimitPrice and StopPrice");

        var limitExit = new Dictionary<string, object>
        {
            ["orderType"]         = "LIMIT",
            ["session"]           = MapSession(req.Session),
            ["duration"]          = MapTif(req.TimeInForce),
            ["orderStrategyType"] = "SINGLE",
            ["price"]             = req.LimitPrice.Value.ToString("F2"),
            ["orderLegCollection"] = new[] { BuildLeg(req) }
        };

        var stopExit = new Dictionary<string, object>
        {
            ["orderType"]         = "STOP",
            ["session"]           = MapSession(req.Session),
            ["duration"]          = MapTif(req.TimeInForce),
            ["orderStrategyType"] = "SINGLE",
            ["stopPrice"]         = req.StopPrice.Value.ToString("F2"),
            ["orderLegCollection"] = new[] { BuildLeg(req) }
        };

        return ToElement(new Dictionary<string, object>
        {
            ["orderStrategyType"] = "OCO",
            ["childOrderStrategies"] = new[] { limitExit, stopExit }
        });
    }

    private static Dictionary<string, object> BuildLeg(OrderRequest req) => new()
    {
        ["instruction"] = MapInstruction(req.Side, req.AssetType),
        ["quantity"]    = req.Quantity,
        ["instrument"]  = new Dictionary<string, string>
        {
            ["symbol"]    = MapSymbol(req.Symbol, req.AssetType),
            ["assetType"] = MapAssetType(req.AssetType)
        }
    };

    private static string MapSymbol(string symbol, AssetType assetType) =>
        assetType == AssetType.Option && TrySplitOptionSymbol(symbol, out var root, out var suffix)
            ? root.PadRight(6) + suffix
            : symbol.Trim().ToUpperInvariant();

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

    private static string MapInstruction(OrderSide side, AssetType assetType) =>
        (side, assetType) switch
        {
            (OrderSide.Buy, AssetType.Option)  => "BUY_TO_OPEN",
            (OrderSide.Sell, AssetType.Option) => "SELL_TO_CLOSE",
            (OrderSide.Buy, _)           => "BUY",
            (OrderSide.Sell, AssetType.Equity or AssetType.ETF) => "SELL",
            _ => side == OrderSide.Buy ? "BUY" : "SELL"
        };

    private static string MapTif(TimeInForce tif) => tif switch
    {
        TimeInForce.GTC => "GOOD_TILL_CANCEL",
        TimeInForce.IOC => "IMMEDIATE_OR_CANCEL",
        TimeInForce.FOK => "FILL_OR_KILL",
        _               => "DAY"
    };

    private static string MapSession(OrderSession session) => session switch
    {
        OrderSession.AM       => "AM",
        OrderSession.PM       => "PM",
        OrderSession.Seamless => "SEAMLESS",
        _                     => "NORMAL"
    };

    private static string MapAssetType(AssetType assetType) => assetType switch
    {
        AssetType.Option     => "OPTION",
        AssetType.MutualFund => "MUTUAL_FUND",
        _                    => "EQUITY"
    };

    private static JsonElement ToElement(object obj)
    {
        var json = JsonSerializer.Serialize(obj);
        return JsonDocument.Parse(json).RootElement.Clone();
    }
}
