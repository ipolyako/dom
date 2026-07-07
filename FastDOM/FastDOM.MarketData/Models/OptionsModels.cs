namespace FastDOM.MarketData.Models;

public enum OptionType { Call, Put }

// One row in the chain grid: both the call and put for a given strike.
public class OptionsChainRow
{
    public decimal Strike { get; init; }
    public string CallSymbol { get; init; } = "";
    public string PutSymbol  { get; init; } = "";

    // Call side
    public decimal? CallBid    { get; init; }
    public decimal? CallAsk    { get; init; }
    public decimal? CallLast   { get; init; }
    public decimal? CallIV     { get; init; }
    public decimal? CallDelta  { get; init; }
    public decimal? CallTheta  { get; init; }
    public int?     CallOI     { get; init; }
    public int?     CallVolume { get; init; }

    // Put side
    public decimal? PutBid    { get; init; }
    public decimal? PutAsk    { get; init; }
    public decimal? PutLast   { get; init; }
    public decimal? PutIV     { get; init; }
    public decimal? PutDelta  { get; init; }
    public decimal? PutTheta  { get; init; }
    public int?     PutOI     { get; init; }
    public int?     PutVolume { get; init; }
}

// A specific contract chosen by the user.
public class SelectedOptionContract
{
    public string OccSymbol  { get; init; } = "";
    public string Underlying { get; init; } = "";
    public DateOnly Expiration { get; init; }
    public decimal Strike    { get; init; }
    public OptionType Type   { get; init; }
    public decimal? Bid      { get; init; }
    public decimal? Ask      { get; init; }
    public decimal? IV       { get; init; }
    public decimal? Delta    { get; init; }

    public string Display =>
        $"{Underlying} {Expiration:MMM dd yy} " +
        $"{(Type == OptionType.Call ? "C" : "P")}{Strike:F0}  " +
        $"Bid {Bid:F2}  Ask {Ask:F2}" +
        (IV.HasValue ? $"  IV {IV.Value:P1}" : "") +
        (Delta.HasValue ? $"  Δ {Delta.Value:F2}" : "");
}
