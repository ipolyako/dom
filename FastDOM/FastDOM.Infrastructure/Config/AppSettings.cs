using FastDOM.Core.Enums;
using FastDOM.Core.Models;

namespace FastDOM.Infrastructure.Config;

public class AppSettings
{
    public TradingMode Mode { get; set; } = TradingMode.Simulation;
    public string LogDirectory { get; set; } = "logs";
    public int MaxLogFileSizeMb { get; set; } = 50;
    public int MaxLogFiles { get; set; } = 10;
    public string DefaultSymbol { get; set; } = "SPY";
    public string DefaultAccountId { get; set; } = string.Empty;
    public List<int> ShareSizePresets { get; set; } = [100, 200, 500];
    public int DefaultShareSize { get; set; } = 100;
    public int DomVisibleLevels { get; set; } = 40;
    public bool AlwaysOnTop { get; set; } = false;
    public bool CompactMode { get; set; } = false;
    public double FontScale { get; set; } = 1.0;
    public bool ConfirmFirstLiveOrder { get; set; } = true;
    public bool FirstLiveOrderConfirmed { get; set; } = false;
    public WindowLayout? LastWindowLayout { get; set; }
}

public class WindowLayout
{
    public double Left { get; set; }
    public double Top { get; set; }
    public double Width { get; set; } = 1200;
    public double Height { get; set; } = 900;
    public bool IsMaximized { get; set; }
}
