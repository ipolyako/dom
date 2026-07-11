namespace FastDOM.MarketData.Models;

public class PriceCandle
{
    public DateTime Timestamp { get; set; }
    public decimal Open { get; set; }
    public decimal High { get; set; }
    public decimal Low { get; set; }
    public decimal Close { get; set; }
    public long Volume { get; set; }
}

public record PriceHistoryRequest(
    string PeriodType,
    int Period,
    string FrequencyType,
    int Frequency,
    bool IncludeExtendedHours = true);
