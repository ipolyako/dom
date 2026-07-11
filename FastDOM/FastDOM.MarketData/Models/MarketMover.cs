namespace FastDOM.MarketData.Models;

public enum MoverSort
{
    Volume,
    Trades,
    PercentChangeUp,
    PercentChangeDown
}

public class MarketMover
{
    public string Symbol { get; init; } = "";
    public string Description { get; set; } = "";
    public decimal LastPrice { get; set; }
    public decimal NetChange { get; set; }
    public decimal NetPercentChange { get; set; }
    public long Volume { get; set; }
    public long Average10DayVolume { get; set; }
    public decimal RelativeVolume => Average10DayVolume > 0 ? (decimal)Volume / Average10DayVolume : 0;
    public string NetPercentDisplay => $"{NetPercentChange:+0.00;-0.00;0.00}%";
    public string RelativeVolumeDisplay => Average10DayVolume > 0 ? $"{RelativeVolume:0.00}x" : "—";
    public long Trades { get; set; }
    public decimal Bid { get; set; }
    public decimal Ask { get; set; }
    public decimal Week52High { get; set; }
    public decimal Week52Low { get; set; }
}
