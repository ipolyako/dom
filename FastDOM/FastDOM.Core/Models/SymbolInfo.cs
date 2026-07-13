using FastDOM.Core.Enums;

namespace FastDOM.Core.Models;

public class SymbolInfo
{
    public required string Symbol { get; init; }
    public required AssetType AssetType { get; init; }
    public string? Description { get; init; }
    public decimal TickSize { get; set; } = 0.01m;
    public int LotSize { get; set; } = 1;
    public bool IsActive { get; set; } = true;

    public static SymbolInfo Default(string symbol)
    {
        symbol = symbol.Trim().ToUpperInvariant();
        var isFuture = symbol.StartsWith('/');
        return new SymbolInfo
        {
            Symbol = symbol,
            AssetType = isFuture ? AssetType.Future : AssetType.Equity,
            TickSize = isFuture ? FuturesTickSize(symbol) : 0.01m,
            LotSize = 1
        };
    }

    private static decimal FuturesTickSize(string symbol)
    {
        var root = new string(symbol.Skip(1).TakeWhile(char.IsLetter).ToArray());
        return root switch
        {
            "ES" or "MES" or "NQ" or "MNQ" => 0.25m,
            "YM" or "MYM" => 1m,
            "RTY" or "M2K" or "GC" or "MGC" => 0.10m,
            "CL" or "MCL" => 0.01m,
            "SI" => 0.005m,
            "HG" => 0.0005m,
            "ZB" => 0.03125m,
            "ZN" => 0.015625m,
            _ => 0.01m
        };
    }

    public decimal RoundToTick(decimal price)
    {
        if (TickSize <= 0) return price;
        return Math.Round(price / TickSize, MidpointRounding.AwayFromZero) * TickSize;
    }

    public bool IsTickAligned(decimal price) => (price % TickSize) == 0;
}
