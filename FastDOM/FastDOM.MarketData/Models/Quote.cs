namespace FastDOM.MarketData.Models;

public class Quote
{
    public required string Symbol { get; init; }
    public decimal Bid { get; set; }
    public decimal Ask { get; set; }
    public decimal Last { get; set; }
    public decimal Open { get; set; }
    public decimal Close { get; set; }
    public decimal High { get; set; }
    public decimal Low { get; set; }
    public long Volume { get; set; }
    public int BidSize { get; set; }
    public int AskSize { get; set; }
    public int LastSize { get; set; }
    public decimal NetChange { get; set; }
    public decimal NetChangePct { get; set; }
    public decimal Spread => Ask - Bid;
    public decimal Mid => (Bid + Ask) / 2m;
    public DateTime TimestampUtc { get; set; } = DateTime.UtcNow;

    public bool IsStale(int thresholdMs) =>
        (DateTime.UtcNow - TimestampUtc).TotalMilliseconds > thresholdMs;

    public int AgeMs => (int)(DateTime.UtcNow - TimestampUtc).TotalMilliseconds;
}

public class DomLevel
{
    public decimal Price { get; set; }
    public int BidSize { get; set; }
    public int AskSize { get; set; }
}

public class MarketDepth
{
    public required string Symbol { get; init; }
    public List<DomLevel> Bids { get; set; } = [];
    public List<DomLevel> Asks { get; set; } = [];
    public DateTime TimestampUtc { get; set; } = DateTime.UtcNow;
    public bool HasRealDepth { get; set; } = false;
}

public class Trade
{
    public required string Symbol { get; init; }
    public decimal Price { get; set; }
    public int Size { get; set; }
    public DateTime TimestampUtc { get; set; } = DateTime.UtcNow;
}
