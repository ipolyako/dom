namespace FastDOM.Core.Models;

public class RiskProfile
{
    public bool LiveTradingEnabled { get; set; } = false;
    public List<string> AccountWhitelist { get; set; } = [];
    public List<string> SymbolWhitelist { get; set; } = [];
    public List<string> SymbolBlacklist { get; set; } = [];
    public int MaxSharesPerOrder { get; set; } = 0;
    public decimal MaxNotionalPerOrder { get; set; } = 0m;
    public decimal MaxPositionNotionalPerSymbol { get; set; } = 0m;
    public decimal MaxDailyLoss { get; set; } = 0m;
    public int MaxDailyOrderCount { get; set; } = 0;
    public int MaxOrdersPerMinute { get; set; } = 0;
    public bool AllowShortSelling { get; set; } = true;
    public bool AllowExtendedHours { get; set; } = true;
    public decimal RequireConfirmationAboveNotional { get; set; } = 0m; // 0 = no confirmation required
    public bool DisableOpeningOrdersWhenMarketDataStale { get; set; } = false;
    public int MarketDataStaleMs { get; set; } = 2500;
    public decimal MaxSpreadForMarketOrders { get; set; } = 0m;
    public bool RequireConfirmAllLiveOrders { get; set; } = false;
}
