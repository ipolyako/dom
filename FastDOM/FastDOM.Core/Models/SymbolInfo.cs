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

    public static SymbolInfo Default(string symbol) => new()
    {
        Symbol = symbol.ToUpperInvariant(),
        AssetType = AssetType.Equity,
        TickSize = 0.01m,
        LotSize = 1
    };

    public decimal RoundToTick(decimal price)
    {
        if (TickSize <= 0) return price;
        return Math.Round(price / TickSize, MidpointRounding.AwayFromZero) * TickSize;
    }

    public bool IsTickAligned(decimal price) => (price % TickSize) == 0;
}
