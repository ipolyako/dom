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
    private readonly DispatcherTimer _statusTimer;

    [ObservableProperty] private string _selectedSymbol = "SPY";
    [ObservableProperty] private string _selectedAccountId = "";
    [ObservableProperty] private int _shareSize = 100;
    [ObservableProperty] private string _tradingModeLabel = "SIM";
    [ObservableProperty] private string _connectionStatus = "Disconnected";
    [ObservableProperty] private bool _isConnected;
    [ObservableProperty] private bool _hotkeysArmed = true;
    [ObservableProperty] private string _lastToast = "";
    [ObservableProperty] private bool _isLiveMode;
    [ObservableProperty] private string _quoteDisplay = "—";
    [ObservableProperty] private string _dataAgeDisplay = "—";
    [ObservableProperty] private bool _marketDataStale;

    public ObservableCollection<string> SymbolList { get; } =
        new(["SPY", "QQQ", "NVDA", "TSLA", "TQQQ", "SQQQ", "AAPL", "MSFT"]);
    public ObservableCollection<AccountInfo> Accounts { get; } = [];
    public ObservableCollection<string> ActivityLog { get; } = [];

    public DomViewModel DomViewModel { get; }
    public PositionViewModel PositionViewModel { get; }
    public HotButtonsViewModel HotButtonsViewModel { get; }
    public OrderTicketViewModel OrderTicketViewModel { get; }

    public MainViewModel(
        ILogger<MainViewModel> logger,
        IBrokerClient broker,
        IMarketDataClient marketData,
        IRiskManager riskManager,
        OrderService orderService,
        DomService domService,
        HotkeyService hotkeyService,
        ConfigManager config,
        DomViewModel domVm,
        PositionViewModel posVm,
        HotButtonsViewModel hotVm,
        OrderTicketViewModel ticketVm)
    {
        _logger = logger;
        _broker = broker;
        _marketData = marketData;
        _riskManager = riskManager;
        _orderService = orderService;
        _domService = domService;
        _hotkeyService = hotkeyService;
        _config = config;

        DomViewModel = domVm;
        PositionViewModel = posVm;
        HotButtonsViewModel = hotVm;
        OrderTicketViewModel = ticketVm;

        IsLiveMode = config.AppSettings.Mode == TradingMode.SchwabLive;
        TradingModeLabel = config.AppSettings.Mode switch
        {
            TradingMode.SchwabLive    => "⚠ LIVE",
            TradingMode.SchwabSandbox => "SANDBOX",
            _                         => "SIM"
        };

        ShareSize = config.AppSettings.DefaultShareSize;
        SelectedSymbol = config.AppSettings.DefaultSymbol;

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
    }

    [RelayCommand]
    private async Task ConnectAsync()
    {
        LogActivity("Connecting...");
        await _broker.ConnectAsync();
        await _marketData.ConnectAsync();
        var accounts = await _broker.GetAccountsAsync();
        Accounts.Clear();
        foreach (var a in accounts) Accounts.Add(a);

        if (accounts.Count > 0 && string.IsNullOrEmpty(SelectedAccountId))
        {
            SelectedAccountId = accounts[0].AccountId;
            PositionViewModel.AccountId = SelectedAccountId;
            DomViewModel.CurrentAccountId = SelectedAccountId;
            OrderTicketViewModel.AccountId = SelectedAccountId;
        }

        await ChangeSymbolAsync();
        LogActivity($"Connected. Mode: {TradingModeLabel}");
    }

    [RelayCommand]
    private async Task ChangeSymbolAsync()
    {
        if (string.IsNullOrEmpty(SelectedSymbol)) return;
        LogActivity($"Subscribing to {SelectedSymbol}...");
        await _domService.SubscribeAsync(SelectedSymbol);
        DomViewModel.Symbol = SelectedSymbol;
        await PositionViewModel.RefreshAsync(SelectedAccountId, SelectedSymbol);
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
        await HotButtonsViewModel.ExecuteActionAsync(actionType, SelectedSymbol, SelectedAccountId, ShareSize);
    }

    private void OnOrderStateChanged(OrderState state)
    {
        Application.Current.Dispatcher.Invoke(() =>
        {
            LogActivity($"Order {state.Status}: {state.Side} {state.QuantityOrdered} {state.Symbol}" +
                        (state.LimitPrice.HasValue ? $" @{state.LimitPrice:F2}" : " MKT"));
            DomViewModel.RefreshOrders(_orderService.ActiveOrders.Values);
        });
    }

    private void OnQuoteUpdated(FastDOM.MarketData.Models.Quote q)
    {
        Application.Current.Dispatcher.Invoke(() =>
        {
            if (q.Symbol != SelectedSymbol) return;
            QuoteDisplay = $"{q.Last:F2}  B:{q.Bid:F2} x {q.BidSize}  A:{q.Ask:F2} x {q.AskSize}" +
                           $"  Chg:{q.NetChange:+0.00;-0.00}  Vol:{q.Volume:N0}";
            MarketDataStale = q.IsStale(_config.RiskProfile.MarketDataStaleMs);
        });
    }

    partial void OnSelectedAccountIdChanged(string value)
    {
        DomViewModel.CurrentAccountId = value;
        OrderTicketViewModel.AccountId = value;
        PositionViewModel.AccountId = value;
    }

    partial void OnSelectedSymbolChanged(string value)
    {
        OrderTicketViewModel.Symbol = value;
        DomViewModel.Symbol = value;
    }

    private void UpdateDataAge()
    {
        if (_domService.CurrentQuote != null)
            DataAgeDisplay = $"{_domService.CurrentQuote.AgeMs}ms";
    }

    private void ShowToast(string message)
    {
        Application.Current.Dispatcher.Invoke(() =>
        {
            LastToast = message;
            LogActivity(message);
        });
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

    private void AppendActivityEntry(string entry)
    {
        if (ActivityLog.Count >= 500) ActivityLog.RemoveAt(0);
        ActivityLog.Add(entry);
    }
}
