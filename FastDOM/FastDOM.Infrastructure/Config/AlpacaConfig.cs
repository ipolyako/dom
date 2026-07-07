namespace FastDOM.Infrastructure.Config;

public class AlpacaConfig
{
    public string ApiKey    { get; set; } = "";
    public string ApiSecret { get; set; } = "";
    public bool   IsPaper   { get; set; } = true;

    public string TraderApiBase  => IsPaper
        ? "https://paper-api.alpaca.markets/v2"
        : "https://api.alpaca.markets/v2";

    public string MarketDataBase => "https://data.alpaca.markets/v2";

    public string StreamBase     => IsPaper
        ? "wss://paper-api.alpaca.markets/stream"
        : "wss://api.alpaca.markets/stream";

    public string DataStreamBase => "wss://stream.data.alpaca.markets/v2/iex";
}
