using System.Text.Json;
using FastDOM.Core.Enums;
using FastDOM.Core.Models;
using Microsoft.Extensions.Logging;

namespace FastDOM.Infrastructure.Config;

public class ConfigManager
{
    private readonly ILogger<ConfigManager> _logger;
    private readonly string _configDir;

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true,
        Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter() },
    };

    public AppSettings AppSettings { get; private set; } = new();
    public SchwabConfig SchwabConfig { get; private set; } = new();
    public AlpacaConfig AlpacaConfig { get; private set; } = new();
    public RiskProfile RiskProfile { get; private set; } = new();
    public HotkeyConfig HotkeyConfig { get; private set; } = new();
    public List<HotButtonConfig> HotButtons { get; private set; } = [];
    public TokenSourceConfig TokenSource { get; private set; } = new();

    public ConfigManager(ILogger<ConfigManager> logger, string? configDir = null)
    {
        _logger = logger;
        _configDir = configDir ?? ResolveConfigDir();
        Directory.CreateDirectory(_configDir);
    }

    private static string ResolveConfigDir()
    {
        var exeDir = AppContext.BaseDirectory;
        if (File.Exists(Path.Combine(exeDir, "appsettings.json")))
            return exeDir;
        return Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "FastDOM");
    }

    public void LoadAll()
    {
        BootstrapFromExamples();
        AppSettings = Load<AppSettings>("appsettings.json") ?? new AppSettings();
        AppSettings.Mode = TradingMode.Simulation; // Always start in SIM — switch to live explicitly
        SchwabConfig = Load<SchwabConfig>("broker.schwab.json") ?? new SchwabConfig();
        AlpacaConfig = Load<AlpacaConfig>("alpaca.json") ?? new AlpacaConfig();
        RiskProfile = Load<RiskProfile>("risk.profile.json") ?? new RiskProfile();
        HotkeyConfig = Load<HotkeyConfig>("hotkeys.json") ?? new HotkeyConfig();
        HotButtons = Load<List<HotButtonConfig>>("hotbuttons.json") ?? DefaultHotButtons();
        TokenSource = Load<TokenSourceConfig>("token.source.json") ?? new TokenSourceConfig();
        _logger.LogInformation("Config loaded from {Dir}", _configDir);
    }

    private void BootstrapFromExamples()
    {
        var exeDir = AppContext.BaseDirectory;
        var pairs = new[]
        {
            ("appsettings.json",   "appsettings.example.json"),
            ("broker.schwab.json", "broker.schwab.example.json"),
            ("alpaca.json",        "alpaca.example.json"),
            ("risk.profile.json",  "risk.profile.example.json"),
        };
        foreach (var (real, example) in pairs)
        {
            var realPath    = Path.Combine(_configDir, real);
            var examplePath = Path.Combine(exeDir, example);
            if (!File.Exists(realPath) && File.Exists(examplePath))
            {
                File.Copy(examplePath, realPath);
                _logger.LogInformation("Created default config {File} from example", real);
            }
        }
    }

    public void SaveAll()
    {
        Save("appsettings.json", AppSettings);
        Save("broker.schwab.json", SchwabConfig);
        Save("risk.profile.json", RiskProfile);
        Save("hotkeys.json", HotkeyConfig);
        Save("hotbuttons.json", HotButtons);
    }

    public void Save(string filename, object obj)
    {
        var path = Path.Combine(_configDir, filename);
        File.WriteAllText(path, JsonSerializer.Serialize(obj, JsonOpts));
    }

    private T? Load<T>(string filename)
    {
        var path = Path.Combine(_configDir, filename);
        if (!File.Exists(path)) return default;
        try
        {
            return JsonSerializer.Deserialize<T>(File.ReadAllText(path), JsonOpts);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to load {File}", filename);
            return default;
        }
    }

    public string GetConfigPath(string filename) => Path.Combine(_configDir, filename);

    private static List<HotButtonConfig> DefaultHotButtons()
    {
        int order = 0;
        HotButtonConfig Btn(string id, string label, string color, HotButtonAction action,
            bool confirm = false, QuantityRule? qty = null) =>
            new()
            {
                Id = id, Label = label, Color = color, Action = action,
                DisplayOrder = order++, RequireConfirmation = confirm,
                QuantityRule = qty ?? new QuantityRule()
            };

        return
        [
            Btn("buy_mkt",    "Buy MKT",      "#1B5E20", HotButtonAction.BuyMarket),
            Btn("sell_mkt",   "Sell MKT",     "#B71C1C", HotButtonAction.SellMarket),
            Btn("buy_ask",    "Buy Ask",      "#2E7D32", HotButtonAction.BuyAsk),
            Btn("sell_bid",   "Sell Bid",     "#C62828", HotButtonAction.SellBid),
            Btn("buy_bid",    "Buy Bid",      "#388E3C", HotButtonAction.BuyBid),
            Btn("sell_ask",   "Sell Ask",     "#D32F2F", HotButtonAction.SellAsk),
            Btn("flatten",    "Flatten",      "#E65100", HotButtonAction.Flatten,    confirm: true),
            Btn("reverse",    "Reverse",      "#6A1B9A", HotButtonAction.Reverse,    confirm: true),
            Btn("cancel_sym", "Cancel Sym",   "#37474F", HotButtonAction.CancelSymbol),
            Btn("cancel_all", "Cancel All",   "#212121", HotButtonAction.CancelAll,  confirm: true),
            Btn("sell_25",    "Sell 25%",     "#BF360C", HotButtonAction.SellPercent,
                qty: new QuantityRule { Type = QuantityRuleType.PercentOfPosition, PercentOfPosition = 25 }),
            Btn("sell_50",    "Sell 50%",     "#C62828", HotButtonAction.SellPercent,
                qty: new QuantityRule { Type = QuantityRuleType.PercentOfPosition, PercentOfPosition = 50 }),
            Btn("sell_100",   "Sell 100%",    "#B71C1C", HotButtonAction.SellPercent,
                qty: new QuantityRule { Type = QuantityRuleType.PercentOfPosition, PercentOfPosition = 100 }),
            Btn("stop_be",    "Stop BE",      "#455A64", HotButtonAction.MoveStopToBreakeven),
        ];
    }
}
