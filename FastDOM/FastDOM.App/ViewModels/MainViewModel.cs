using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using FastDOM.App.Services;
using FastDOM.Broker.Interfaces;
using FastDOM.Core.Enums;
using FastDOM.Core.Models;
using FastDOM.Infrastructure.Config;
using FastDOM.MarketData.Interfaces;
using Microsoft.Extensions.Logging;

namespace FastDOM.App.ViewModels;

public record TradingModeOption(string Label, TradingMode Mode)
{
    public override string ToString() => Label;
}

public partial class MainViewModel : ObservableObject
{
    private readonly ILogger<MainViewModel> _logger;
    private readonly IBrokerClient _broker;
    private readonly IMarketDataClient _marketData;
    private readonly IRiskManager _riskManager;
    private readonly OrderService _orderService;
    private readonly DomService _domService;
    private readonly HotkeyService _hotkeyService;
    private readonly ConfigManager _config;
    private readonly BrokerFactory _brokerFactory;
    private readonly DispatcherTimer _statusTimer;
    private readonly DispatcherTimer _accountSyncTimer;
    private bool _accountSyncInFlight;

    [ObservableProperty] private string _selectedSymbol = "SPY";
    [ObservableProperty] private string _selectedAccountId = "";
    [ObservableProperty] private int _shareSize = 100;
    [ObservableProperty] private string _tradingModeLabel = "SIM";
    [ObservableProperty] private string _connectionStatus = "Disconnected";
    [ObservableProperty] private bool _isConnected;
    [ObservableProperty] private bool _hotkeysArmed = true;
    [ObservableProperty] private bool _hasOpenOrders;
    [ObservableProperty] private string _lastToast = "";
    [ObservableProperty] private bool _isLiveMode;
    [ObservableProperty] private string _quoteDisplay = "—";
    [ObservableProperty] private string _dataAgeDisplay = "—";
    [ObservableProperty] private bool _marketDataStale;
    [ObservableProperty] private string _buyingPowerDisplay = "—";
    [ObservableProperty] private string _buyingPowerTooltip = "";
    [ObservableProperty] private bool _showDepthMap;

    public ObservableCollection<AccountInfo> Accounts { get; } = [];
    public ObservableCollection<string> ActivityLog { get; } = [];

    public static IReadOnlyList<TradingModeOption> ModeOptions { get; } =
    [
        new("SIM",          TradingMode.Simulation),
        new("Schwab Live",  TradingMode.SchwabLive),
        new("Alpaca Paper", TradingMode.AlpacaPaper),
        new("Alpaca Live",  TradingMode.AlpacaLive),
    ];

    [ObservableProperty] private TradingModeOption _selectedMode =
        ModeOptions.First(m => m.Mode == TradingMode.Simulation);

    public DomViewModel DomViewModel { get; }
    public PositionViewModel PositionViewModel { get; }
    public HotButtonsViewModel HotButtonsViewModel { get; }
    public OrderTicketViewModel OrderTicketViewModel { get; }
    public WatchlistViewModel WatchlistViewModel { get; }
    public DepthMapViewModel DepthMapViewModel { get; }

    public MainViewModel(
        ILogger<MainViewModel> logger,
        IBrokerClient broker,
        IMarketDataClient marketData,
        IRiskManager riskManager,
        OrderService orderService,
        DomService domService,
        HotkeyService hotkeyService,
        ConfigManager config,
        BrokerFactory brokerFactory,
        DomViewModel domVm,
        PositionViewModel posVm,
        HotButtonsViewModel hotVm,
        OrderTicketViewModel ticketVm,
        WatchlistViewModel watchlistVm,
        DepthMapViewModel depthMapVm)
    {
        _logger = logger;
        _broker = broker;
        _marketData = marketData;
        _riskManager = riskManager;
        _orderService = orderService;
        _domService = domService;
        _hotkeyService = hotkeyService;
        _config = config;
        _brokerFactory = brokerFactory;

        DomViewModel = domVm;
        PositionViewModel = posVm;
        HotButtonsViewModel = hotVm;
        OrderTicketViewModel = ticketVm;
        WatchlistViewModel = watchlistVm;
        DepthMapViewModel = depthMapVm;

        watchlistVm.SymbolSelected += sym =>
        {
            SelectedSymbol = sym;
            _ = ChangeSymbolCommand.ExecuteAsync(null);
        };

        posVm.SymbolSelected += sym =>
        {
            SelectedSymbol = sym;
            _ = ChangeSymbolCommand.ExecuteAsync(null);
        };

        var initialMode = config.AppSettings.Mode;
        _selectedMode = ModeOptions.FirstOrDefault(m => m.Mode == initialMode) ?? ModeOptions[0];
        IsLiveMode = initialMode != TradingMode.Simulation;
        TradingModeLabel = ModeLabel(initialMode);

        ShareSize = config.AppSettings.DefaultShareSize;
        SelectedSymbol = config.AppSettings.DefaultSymbol;
        ShowDepthMap = config.AppSettings.ShowDepthMap;

        _broker.ConnectionStateStream.Subscribe(connected =>
            Application.Current.Dispatcher.Invoke(() =>
            {
                IsConnected = connected;
                ConnectionStatus = connected ? "Connected" : "Disconnected";
            }));

        _marketData.ConnectionStateStream.Subscribe(connected =>
            Application.Current.Dispatcher.Invoke(() =>
                DataAgeDisplay = connected ? "Live" : "DISCONNECTED"));

        _orderService.OrderStateChanged += OnOrderStateChanged;
        _orderService.ToastRequested += ShowToast;
        _domService.QuoteUpdated += OnQuoteUpdated;

        _statusTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(500) };
        _statusTimer.Tick += (_, _) => UpdateDataAge();
        _statusTimer.Start();

        // Reconcile external Schwab/TOS changes for the selected account.
        // Market data uses the separate DATA token; this TRADE-token sync is
        // intentionally bounded and still backs off when Schwab returns 429.
        _accountSyncTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(5) };
        _accountSyncTimer.Tick += async (_, _) => await SyncExternalAccountStateAsync();
        _accountSyncTimer.Start();
    }

    partial void OnSelectedModeChanged(TradingModeOption value)
        => _ = ChangeModeAsync(value.Mode);

    private static string ModeLabel(TradingMode m) => m switch
    {
        TradingMode.SchwabLive   => "⚠ SCHWAB LIVE",
        TradingMode.AlpacaPaper  => "ALPACA PAPER",
        TradingMode.AlpacaLive   => "⚠ ALPACA LIVE",
        _                        => "SIM",
    };

    private async Task ChangeModeAsync(TradingMode mode)
    {
        LogActivity($"Switching to {ModeLabel(mode)}...");
        var oldMode = _config.AppSettings.Mode;

        try
        {
            await _brokerFactory.SwitchModeAsync(mode);
            IsLiveMode = mode != TradingMode.Simulation;
            TradingModeLabel = ModeLabel(mode);
            _config.AppSettings.Mode = mode;
            await ConnectAsync();
            _config.Save("appsettings.json", _config.AppSettings);
        }
        catch (Exception ex)
        {
            LogActivity($"Switch failed: {ex.Message}");
            _selectedMode = ModeOptions.First(m => m.Mode == oldMode);
            OnPropertyChanged(nameof(SelectedMode));
            IsLiveMode = oldMode != TradingMode.Simulation;
            TradingModeLabel = ModeLabel(oldMode);
        }
    }

    [RelayCommand]
    private async Task ConnectAsync()
    {
        LogActivity("Connecting...");
        try { await _broker.ConnectAsync(); }
        catch (Exception ex)
        {
            LogActivity($"Connect failed: {ex.Message}");
            return;
        }

        try { await _marketData.ConnectAsync(); }
        catch (Exception ex)
        {
            LogActivity($"Market data connect failed: {ex.Message}");
        }

        IReadOnlyList<AccountInfo> accounts = [];
        try { accounts = await _broker.GetAccountsAsync(); }
        catch (Exception ex)
        {
            LogActivity($"GetAccounts failed: {ex.Message}");
        }

        Accounts.Clear();
        foreach (var a in accounts) Accounts.Add(a);
        if (accounts.Count == 0)
        {
            LastToast = "No accounts returned. Verify Schwab token source and DB access.";
        }

        if (accounts.Count > 0)
        {
            var preferred = _config.AppSettings.DefaultAccountId;
            var toUse = accounts.FirstOrDefault(a =>
                !string.IsNullOrWhiteSpace(preferred) &&
                (string.Equals(a.AccountId, preferred, StringComparison.OrdinalIgnoreCase) ||
                 a.AccountId.EndsWith(preferred, StringComparison.OrdinalIgnoreCase)))?.AccountId
                ?? accounts[0].AccountId;

            if (string.IsNullOrEmpty(SelectedAccountId) || !accounts.Any(a => a.AccountId == SelectedAccountId))
            {
                SelectedAccountId = toUse;
            }
            else
            {
                PositionViewModel.AccountId = SelectedAccountId;
                DomViewModel.CurrentAccountId = SelectedAccountId;
                OrderTicketViewModel.AccountId = SelectedAccountId;
            }
        }

        await RefreshForSelectedAccountAsync(SelectedAccountId);
        LogActivity($"Connected. Mode: {TradingModeLabel}");
    }

    private async Task RefreshBuyingPowerAsync()
    {
        if (string.IsNullOrEmpty(SelectedAccountId)) return;
        try
        {
            var summary = await _broker.GetAccountSummaryAsync(SelectedAccountId);
            Application.Current.Dispatcher.Invoke(() =>
            {
                var bp   = summary.BuyingPower;
                var dtbp = summary.DayTradingBuyingPower;
                if (dtbp.HasValue && dtbp > 0)
                {
                    BuyingPowerDisplay = $"${dtbp.Value:N0}";
                    BuyingPowerTooltip = $"Day Trading BP: ${dtbp.Value:N2}"
                        + (bp.HasValue ? $"\nBuying Power: ${bp.Value:N2}" : "")
                        + (summary.NetLiquidation.HasValue ? $"\nEquity: ${summary.NetLiquidation.Value:N2}" : "");
                }
                else if (bp.HasValue && bp > 0)
                {
                    BuyingPowerDisplay = $"${bp.Value:N0}";
                    BuyingPowerTooltip = $"Buying Power: ${bp.Value:N2}"
                        + (summary.NetLiquidation.HasValue ? $"\nEquity: ${summary.NetLiquidation.Value:N2}" : "");
                }
                else
                {
                    BuyingPowerDisplay = "—";
                    BuyingPowerTooltip = "";
                }
            });
        }
        catch { /* non-critical — buying power display stays at last value */ }
    }

    private async Task RefreshForSelectedAccountAsync(string accountId)
    {
        if (string.IsNullOrWhiteSpace(accountId)) return;

        try
        {
            DomViewModel.WorkingOrders.Clear();
            await _orderService.SyncOrdersAsync(accountId);
        }
        catch
        {
            // Non-blocking: if sync fails, fall back to existing cached orders and continue refresh.
        }

        await RefreshBuyingPowerAsync();
        await ChangeSymbolAsync();
    }

    private async Task SyncExternalAccountStateAsync()
    {
        if (_accountSyncInFlight || !IsConnected || string.IsNullOrWhiteSpace(SelectedAccountId))
            return;

        _accountSyncInFlight = true;
        try
        {
            await _orderService.SyncOrdersAsync(SelectedAccountId);

            await PositionViewModel.RefreshAsync(SelectedAccountId, SelectedSymbol);

            await RefreshBuyingPowerAsync();
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "External Schwab account sync failed");
        }
        finally
        {
            _accountSyncInFlight = false;
        }
    }

    [RelayCommand]
    private async Task ChangeSymbolAsync()
    {
        if (string.IsNullOrEmpty(SelectedSymbol)) return;
        LogActivity($"Subscribing to {SelectedSymbol}...");
        await _domService.SubscribeAsync(SelectedSymbol);
        DomViewModel.Symbol = SelectedSymbol;
        await PositionViewModel.RefreshAsync(SelectedAccountId, SelectedSymbol);
        await WatchlistViewModel.ResubscribeAsync();
        LogActivity($"Subscribed to {SelectedSymbol}");
    }

    [RelayCommand]
    private void ToggleHotkeys()
    {
        if (HotkeysArmed) { _hotkeyService.Disarm(); HotkeysArmed = false; }
        else { _hotkeyService.Arm(); HotkeysArmed = true; }
        LogActivity($"Hotkeys {(HotkeysArmed ? "ARMED" : "DISARMED")}");
    }

    public async Task ExecuteHotkeyActionAsync(string actionType)
    {
        LogActivity($"Hotkey: {actionType}");
        await HotButtonsViewModel.ExecuteActionAsync(actionType, SelectedSymbol, SelectedAccountId,
            ShareSize, _domService.CurrentQuote);
    }

    private void OnOrderStateChanged(OrderState state)
    {
        // BeginInvoke @ Background: doesn't block the broker/stream thread, and
        // WPF coalesces bursts of same-priority operations.
        Application.Current.Dispatcher.BeginInvoke(DispatcherPriority.Background, new Action(() =>
        {
            var priceStr = state.AverageFillPrice.HasValue
                ? $" @{state.AverageFillPrice:F2}"
                : state.LimitPrice.HasValue ? $" @{state.LimitPrice:F2}"
                : state.StopPrice.HasValue ? $" STOP @{state.StopPrice:F2}"
                : " MKT";
            LogActivity($"Order {state.Status}: {state.Side} {state.QuantityOrdered} {state.Symbol}{priceStr}");
            // Each order is stored under both ClientOrderId and BrokerOrderId keys; dedupe.
            var uniqueOrders = _orderService.ActiveOrders.Values
                .DistinctBy(o => o.BrokerOrderId ?? o.ClientOrderId)
                .ToList();
            DomViewModel.RefreshOrders(uniqueOrders);
            HasOpenOrders = uniqueOrders.Any(o => o.IsWorking);

            if (state.Symbol == SelectedSymbol &&
                state.Status is OrderStatus.Filled or OrderStatus.PartiallyFilled)
            {
                _ = PositionViewModel.RefreshAsync(SelectedAccountId, state.Symbol);
                _ = RefreshBuyingPowerAsync();
            }
        }));
    }

    // Quote stream fires many times per second on a busy symbol. Instead of
    // marshaling per-tick, latch the newest quote and let a 60ms Render-priority
    // pump apply it. Multiple bursty ticks collapse to a single UI update.
    private FastDOM.MarketData.Models.Quote? _latestQuote;
    private bool _quoteFlushScheduled;

    private void OnQuoteUpdated(FastDOM.MarketData.Models.Quote q)
    {
        _latestQuote = q;
        if (_quoteFlushScheduled) return;
        _quoteFlushScheduled = true;
        Application.Current.Dispatcher.BeginInvoke(DispatcherPriority.Background, new Action(FlushLatestQuote));
    }

    private void FlushLatestQuote()
    {
        _quoteFlushScheduled = false;
        var q = _latestQuote;
        if (q == null || q.Symbol != SelectedSymbol) return;
        QuoteDisplay = $"{q.Last:F2}  B:{q.Bid:F2} x {q.BidSize}  A:{q.Ask:F2} x {q.AskSize}" +
                       $"  Chg:{q.NetChange:+0.00;-0.00}  Vol:{q.Volume:N0}";
        MarketDataStale = q.IsStale(_config.RiskProfile.MarketDataStaleMs);
        PositionViewModel.UpdateFromQuote(q.Last);
        OrderTicketViewModel.LastPrice = q.Last;
    }

    public string SelectedAccountDisplay =>
        string.IsNullOrEmpty(SelectedAccountId) ? "—"
        : SelectedAccountId.Length <= 12
            ? SelectedAccountId
            : SelectedAccountId.Substring(0, 4) + "···" + SelectedAccountId.Substring(SelectedAccountId.Length - 4);

    partial void OnSelectedAccountIdChanged(string value)
    {
        DomViewModel.CurrentAccountId = value;
        OrderTicketViewModel.AccountId = value;
        PositionViewModel.AccountId = value;
        OnPropertyChanged(nameof(SelectedAccountDisplay));

        _ = RefreshForSelectedAccountAsync(value);
    }

    partial void OnSelectedSymbolChanged(string value)
    {
        OrderTicketViewModel.ResetForSymbol(value);
        DomViewModel.Symbol = value;
    }

    partial void OnShareSizeChanged(int value)
    {
        OrderTicketViewModel.Quantity = value;
    }

    partial void OnShowDepthMapChanged(bool value)
    {
        _config.AppSettings.ShowDepthMap = value;
        _config.Save("appsettings.json", _config.AppSettings);
    }

    private void UpdateDataAge()
    {
        if (_domService.CurrentQuote != null)
            DataAgeDisplay = $"{_domService.CurrentQuote.AgeMs}ms";
    }

    private void ShowToast(string message)
    {
        Application.Current.Dispatcher.BeginInvoke(DispatcherPriority.Background, new Action(() =>
        {
            LastToast = message;
            LogActivity(message);
        }));
    }

    private void LogActivity(string message)
    {
        var entry = $"{DateTime.Now:HH:mm:ss.fff} {message}";
        _logger.LogInformation("{Entry}", entry);
        if (Application.Current?.Dispatcher.CheckAccess() == false)
        {
            Application.Current.Dispatcher.InvokeAsync(() => AppendActivityEntry(entry));
        }
        else
        {
            AppendActivityEntry(entry);
        }
    }

    // Cap the log at ~500 entries but trim in a single burst of 100 rather than
    // RemoveAt(0) on every add. RemoveAt(0) is O(n) on ObservableCollection and
    // raises a CollectionChanged Remove per call — expensive on a busy session
    // (10-20 events/sec). Batching cuts that to 1 shift per 100 entries.
    private const int LogCap = 500;
    private const int LogTrimBatch = 100;

    private void AppendActivityEntry(string entry)
    {
        if (ActivityLog.Count >= LogCap)
        {
            // ObservableCollection has no RemoveRange, but Reset is a single
            // notification: build a new list and swap contents.
            var kept = new List<string>(LogCap);
            for (int i = LogTrimBatch; i < ActivityLog.Count; i++) kept.Add(ActivityLog[i]);
            ActivityLog.Clear();
            foreach (var s in kept) ActivityLog.Add(s);
        }
        ActivityLog.Add(entry);
    }
}
