using FastDOM.Core.Enums;

namespace FastDOM.App.Services;

public static class SymbolClassifier
{
    public static AssetType AssetTypeFor(string symbol) =>
        IsOptionSymbol(symbol) ? AssetType.Option
        : IsFutureSymbol(symbol) ? AssetType.Future
        : AssetType.Equity;

    public static bool IsFutureSymbol(string symbol) =>
        symbol.TrimStart().StartsWith('/');

    public static string NormalizeDisplaySymbol(string symbol)
    {
        symbol = symbol.Trim().ToUpperInvariant();
        return TrySplitOptionSymbol(symbol, out var root, out var suffix)
            ? root.Trim() + suffix
            : symbol;
    }

    public static bool IsOptionSymbol(string symbol)
    {
        symbol = symbol.Trim().ToUpperInvariant();
        return TrySplitOptionSymbol(symbol, out _, out _);
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
            return true;
        }

        return false;
    }
}
