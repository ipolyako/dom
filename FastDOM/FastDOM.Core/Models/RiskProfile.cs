namespace FastDOM.Core.Models;

public class RiskProfile
{
    public bool LiveTradingEnabled { get; set; } = false;
    public List<string> AccountWhitelist { get; set; } = [];
    public List<string> SymbolWhitelist { get; set; } = [];
    public List<string> SymbolBlacklist { get; set; } = [];
    public int MaxSharesPerOrder { get; set; } = 0;
    public decimal MaxNotionalPerOrder { get; set; } = 1_000_000m;
    public decimal MaxPositionNotionalPerSymbol { get; set; } = 5_000_000m;
    public decimal MaxDailyLoss { get; set; } = 500m;
    public int MaxDailyOrderCount { get; set; } = 500;
    public int MaxOrdersPerMinute { get; set; } = 20;
    public bool AllowShortSelling { get; set; } = false;
    public bool AllowExtendedHours { get; set; } = false;
    public decimal RequireConfirmationAboveNotional { get; set; } = 0m; // 0 = no confirmation required
    public bool DisableOpeningOrdersWhenMarketDataStale { get; set; } = false;
    public int MarketDataStaleMs { get; set; } = 2500;
    public decimal MaxSpreadForMarketOrders { get; set; } = 0.05m;
    public bool RequireConfirmAllLiveOrders { get; set; } = false;
}
