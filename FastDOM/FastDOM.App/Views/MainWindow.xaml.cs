using System.Diagnostics;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using FastDOM.App.Services;
using FastDOM.App.ViewModels;
using Microsoft.Extensions.DependencyInjection;
using FastDOM.Broker.Interfaces;
using FastDOM.Core.Enums;
using FastDOM.Core.Models;
using FastDOM.Infrastructure.Config;
using FastDOM.Infrastructure.Logging;

namespace FastDOM.App.Views;

public partial class MainWindow : Window
{
    private readonly MainViewModel _vm;
    private readonly OrderService _orderService;
    private readonly HotkeyService _hotkeyService;
    private readonly AuditLogger _audit;
    private readonly ConfigManager _config;
    private readonly IBrokerClient _broker;
    private readonly DomService _domService;
    private readonly AccountSummaryCache _accountCache;
    private readonly IServiceProvider _services;
    private readonly HashSet<string> _ordersBeingMoved = [];
    private bool _killSwitchPending;
    private Thread? _bookmapThread;
    private Dispatcher? _bookmapDispatcher;
    private BookmapWindow? _bookmapWindow;
    private readonly object _chartThreadGate = new();
    private readonly Queue<(string Symbol, string AccountId, int Quantity)> _pendingCharts = [];
    private readonly HashSet<ChartWindow> _chartWindows = [];
    private Thread? _chartThread;
    private Dispatcher? _chartDispatcher;
    private bool _chartShutdownRequested;
    private MoversWindow? _moversWindow;

    public MainWindow(
        MainViewModel vm,
        OrderService orderService,
        HotkeyService hotkeyService,
        AuditLogger audit,
        ConfigManager config,
        IBrokerClient broker,
        DomService domService,
        IServiceProvider services,
        AccountSummaryCache accountCache)
    {
        InitializeComponent();
        DataContext = _vm = vm;
        _orderService = orderService;
        _hotkeyService = hotkeyService;
        _audit = audit;
        _config = config;
        _broker = broker;
        _domService = domService;
        _services = services;
        _accountCache = accountCache;

        // Auto-scroll activity log — defer to Background priority so the ItemsControl
        // finishes processing CollectionChanged before ScrollIntoView triggers layout.
        _vm.ActivityLog.CollectionChanged += (_, _) =>
            Dispatcher.InvokeAsync(
                () => { if (ActivityListBox.Items.Count > 0) ActivityListBox.ScrollIntoView(ActivityListBox.Items[ActivityListBox.Items.Count - 1]); },
                System.Windows.Threading.DispatcherPriority.Background);

        // Wire DOM click events
        _vm.DomViewModel.PriceLevelClicked += OnDomPriceLevelClickedInternal;
        _vm.DomViewModel.OrderMoveRequested += OnOrderMoveRequested;
        _vm.DomViewModel.DragError += msg => Dispatcher.Invoke(() => _vm.LastToast = msg);

        // Wire hot buttons
        _vm.HotButtonsViewModel.ToastRequested += msg =>
            Dispatcher.Invoke(() => _vm.LastToast = msg);

        // Keep the TextBox in sync when SelectedSymbol changes from code (position click, etc.)
        _vm.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(MainViewModel.SelectedSymbol))
                SymbolTextBox.Text = _vm.SelectedSymbol;
        };
    }

    protected override async void OnContentRendered(EventArgs e)
    {
        base.OnContentRendered(e);
        await _vm.ConnectCommand.ExecuteAsync(null);
    }

    private void Window_KeyDown(object sender, KeyEventArgs e)
    {
        var gesture = HotkeyService.BuildGestureString(e);

        // 1. Hot button shortcuts take priority over fixed bindings (explicit user assignment wins)
        if (Keyboard.FocusedElement is TextBox && Keyboard.Modifiers == ModifierKeys.None)
            return;

        var btn = _config.HotButtons.FirstOrDefault(b =>
            b.IsEnabled &&
            !string.IsNullOrWhiteSpace(b.KeyboardShortcut) &&
            string.Equals(b.KeyboardShortcut, gesture, StringComparison.OrdinalIgnoreCase));
        if (btn != null)
        {
            e.Handled = true;
            _ = Task.Run(async () =>
            {
                var summary = await _accountCache.GetAsync(_vm.SelectedAccountId);
                summary.Positions.TryGetValue(_vm.SelectedSymbol, out var pos);
                await _vm.HotButtonsViewModel.ExecuteButtonAsync(
                    btn, _vm.SelectedSymbol, _vm.SelectedAccountId,
                    _vm.ShareSize, _domService.CurrentQuote, pos);
            });
            return;
        }

        // 2. Fixed hotkey bindings (HotkeyConfig)
        var action = _hotkeyService.ProcessKeyDown(e);
        if (action != null)
        {
            e.Handled = true;
            _ = _vm.ExecuteHotkeyActionAsync(action);
        }
    }

    private void SymbolTextBox_GotKeyboardFocus(object sender, KeyboardFocusChangedEventArgs e)
        => ((TextBox)sender).SelectAll();

    private void SymbolTextBox_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        var tb = (TextBox)sender;
        if (!tb.IsKeyboardFocusWithin)
        {
            e.Handled = true;   // prevent caret repositioning
            tb.Focus();
            tb.SelectAll();
        }
    }

    private async void SymbolTextBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter) return;
        var text = SymbolTextBox.Text?.Trim().ToUpperInvariant();
        if (string.IsNullOrWhiteSpace(text)) return;
        e.Handled = true;
        _vm.SelectedSymbol = text;
        await _vm.ChangeSymbolCommand.ExecuteAsync(null);
        // Select all so next keystroke immediately replaces the symbol
        Dispatcher.InvokeAsync(() =>
        {
            SymbolTextBox.Focus();
            SymbolTextBox.SelectAll();
        }, System.Windows.Threading.DispatcherPriority.Input);
    }

    private void MoversButton_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            if (_moversWindow is { IsLoaded: true })
            {
                if (_moversWindow.WindowState == WindowState.Minimized) _moversWindow.WindowState = WindowState.Normal;
                _moversWindow.Activate();
                return;
            }

            var window = new MoversWindow(_services.GetRequiredService<MoversViewModel>()) { Owner = this };
            window.SymbolSelected += async symbol =>
            {
                _vm.SelectedSymbol = symbol;
                await _vm.ChangeSymbolCommand.ExecuteAsync(null);
            };
            window.Closed += (_, _) => _moversWindow = null;
            _moversWindow = window;
            window.Show();
        }
        catch (Exception ex)
        {
            _vm.LastToast = $"Movers failed: {ex.Message}";
            MessageBox.Show(this, ex.ToString(), "Movers failed", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void ChartButton_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            lock (_chartThreadGate)
            {
                _pendingCharts.Enqueue((ActiveDomSymbol(), _vm.SelectedAccountId, _vm.ShareSize));
                if (_chartDispatcher != null)
                {
                    _chartDispatcher.BeginInvoke(OpenPendingChartWindows, DispatcherPriority.Normal);
                    return;
                }
                if (_chartThread != null) return;

                _chartShutdownRequested = false;
                _chartThread = new Thread(ChartThreadMain)
                {
                    Name = "FastDOM Chart UI",
                    IsBackground = true
                };
                _chartThread.SetApartmentState(ApartmentState.STA);
                _chartThread.Start();
            }
        }
        catch (Exception ex)
        {
            _vm.LastToast = $"Chart failed: {ex.Message}";
            MessageBox.Show(this, ex.ToString(), "Chart failed", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void ChartThreadMain()
    {
        SynchronizationContext.SetSynchronizationContext(
            new DispatcherSynchronizationContext(Dispatcher.CurrentDispatcher));

        lock (_chartThreadGate)
        {
            _chartDispatcher = Dispatcher.CurrentDispatcher;
            if (_chartShutdownRequested)
            {
                _pendingCharts.Clear();
                _chartDispatcher = null;
                _chartThread = null;
                return;
            }
        }

        OpenPendingChartWindows();
        Dispatcher.Run();

        lock (_chartThreadGate)
        {
            _chartWindows.Clear();
            _chartDispatcher = null;
            _chartThread = null;
        }
    }

    private void OpenPendingChartWindows()
    {
        while (true)
        {
            (string Symbol, string AccountId, int Quantity) request;
            lock (_chartThreadGate)
            {
                if (_chartShutdownRequested || _pendingCharts.Count == 0) return;
                request = _pendingCharts.Dequeue();
            }

            var window = new ChartWindow(
                _services.GetRequiredService<ChartViewModel>(),
                request.Symbol,
                request.AccountId,
                request.Quantity,
                _vm.HotButtonsViewModel);
            _chartWindows.Add(window);
            window.Closed += (_, _) => _chartWindows.Remove(window);
            window.Show();
        }
    }

    private void HotkeySettings_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new HotkeySettingsWindow(_config.HotkeyConfig, _config) { Owner = this };
        dlg.ShowDialog();
    }

    private void OnHotButtonSettingsRequested(object sender, RoutedEventArgs e)
    {
        var dlg = new HotButtonSettingsWindow(_config.HotButtons, _config) { Owner = this };
        dlg.ShowDialog();
        // Refresh the hot buttons panel so label/color changes appear immediately
        _vm.HotButtonsViewModel.RefreshButtons();
    }

    private void SizePreset_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button btn && int.TryParse(btn.Tag?.ToString(), out var size))
            _vm.ShareSize = size;
    }

    private async void OnOrderTicketQuickAction(object sender, QuickActionEventArgs e)
    {
        var symbol = ActiveDomSymbol();
        var summary = await _accountCache.GetAsync(_vm.SelectedAccountId);
        summary.Positions.TryGetValue(symbol, out var pos);
        await _vm.HotButtonsViewModel.ExecuteActionAsync(
            e.Action, symbol, _vm.SelectedAccountId, _vm.ShareSize, _domService.CurrentQuote, pos);
    }

    private async void KillSwitch_Click(object sender, RoutedEventArgs e)
    {
        if (!_killSwitchPending)
        {
            _killSwitchPending = true;
            KillSwitchButton.Content = "⚠ CONFIRM: Click again to KILL";
            KillSwitchButton.Background = System.Windows.Media.Brushes.DarkRed;
            await Task.Delay(3000);
            if (_killSwitchPending)
            {
                _killSwitchPending = false;
                KillSwitchButton.Content = "⚠ KILL SWITCH — CANCEL + FLATTEN";
                KillSwitchButton.Background = System.Windows.Media.Brushes.Red;
            }
            return;
        }

        _killSwitchPending = false;
        KillSwitchButton.Content = "KILL SWITCH ACTIVATED";
        var symbol = ActiveDomSymbol();

        await _audit.LogKillSwitchAsync(_vm.SelectedAccountId, symbol, "CancelAll+Flatten");

        await _orderService.CancelAllForSymbolFastAsync(_vm.SelectedAccountId, symbol);

        var summary = await _accountCache.GetAsync(_vm.SelectedAccountId);
        if (summary.Positions.TryGetValue(symbol, out var pos) && !pos.IsFlat)
        {
            var side = pos.Side == PositionSide.Long ? OrderSide.Sell : OrderSide.Buy;
            var req = new OrderRequest
            {
                AccountId = _vm.SelectedAccountId,
                Symbol    = symbol,
                AssetType = SymbolClassifier.AssetTypeFor(symbol),
                Side      = side,
                Quantity  = Math.Abs(pos.Quantity),
                OrderType = OrderType.Market,
                Source    = OrderSource.System
            };
            await _orderService.SubmitOrderAsync(req, summary, null, bypassConfirmation: true);
        }

        MessageBox.Show("Kill switch executed. Check positions and orders immediately.",
            "Kill Switch", MessageBoxButton.OK, MessageBoxImage.Warning);
    }

    private async void OnDomPriceLevelClickedInternal(decimal price, OrderSide side, OrderType orderType)
    {
        var symbol = ActiveDomSymbol();
        // Populate ticket for visibility
        _vm.OrderTicketViewModel.PopulateFromDomClick(price, side, orderType);

        // One-click order submission — standard DOM behavior
        var req = new OrderRequest
        {
            AccountId   = _vm.SelectedAccountId,
            Symbol      = symbol,
            AssetType   = SymbolClassifier.AssetTypeFor(symbol),
            Side        = side,
            Quantity    = _vm.ShareSize,
            OrderType   = orderType,
            LimitPrice  = orderType is OrderType.Limit or OrderType.StopLimit or OrderType.MarketableLimit
                          ? price : null,
            StopPrice   = orderType is OrderType.StopMarket or OrderType.StopLimit ? price : null,
            Source      = OrderSource.DomClick
        };

        var account = await _accountCache.GetAsync(_vm.SelectedAccountId);
        var (success, msg) = await _orderService.SubmitOrderAsync(req, account, _domService.CurrentQuote);
        _vm.LastToast = success
            ? $"{side} {_vm.ShareSize} {symbol} @ {price:F2} SENT"
            : $"REJECTED: {msg}";
    }

    private async void OnOrderMoveRequested(OrderState order, decimal fromPrice, decimal newPrice)
    {
        var uiSw = Stopwatch.StartNew();
        if (string.IsNullOrEmpty(order.BrokerOrderId)) return;
        // Serialize by broker identity, not by source/target prices. A second
        // drag of the same order must not overlap while Schwab is replacing it
        // and assigning the successor order id.
        var moveKey = BuildMoveKey(order);
        if (!_ordersBeingMoved.Add(moveKey))
            return;

        var moveStop = IsSamePrice(order.StopPrice, fromPrice)
                       && (!IsSamePrice(order.LimitPrice, fromPrice)
                           || Math.Abs(newPrice - order.StopPrice!.Value) <= Math.Abs(newPrice - (order.LimitPrice ?? newPrice)));
        var replacement = new OrderReplace
        {
            OriginalClientOrderId = order.ClientOrderId,
            BrokerOrderId = order.BrokerOrderId,
            NewLimitPrice = moveStop ? null : newPrice,
            NewStopPrice = moveStop ? newPrice : null,
            Source = OrderSource.DomClick
        };
        try
        {
            Serilog.Log.Information(
                "[LATENCY] DOM move intent order={OrderId} symbol={Symbol} from={FromPrice:F2} to={NewPrice:F2} moveStop={MoveStop} elapsedMs={ElapsedMs}",
                order.BrokerOrderId, order.Symbol, fromPrice, newPrice, moveStop, uiSw.ElapsedMilliseconds);
            var (ok, msg) = await _orderService.ReplaceOrderAsync(_vm.SelectedAccountId, replacement);
            Serilog.Log.Information(
                "[LATENCY] DOM move completed order={OrderId} symbol={Symbol} ok={Ok} message={Message} totalMs={ElapsedMs}",
                order.BrokerOrderId, order.Symbol, ok, msg, uiSw.ElapsedMilliseconds);
            _vm.LastToast = ok ? $"Order moved to {newPrice:F2}" : $"Move failed: {msg}";
        }
        catch (Exception ex)
        {
            // async-void handler — an unhandled exception here surfaces as a
            // modal "FastDOM Error" dialog. Surface it as a toast instead.
            _vm.LastToast = $"Move failed: {ex.GetType().Name} — {ex.Message}";
        }
        finally
        {
            _ordersBeingMoved.Remove(moveKey);
        }
    }

    private static bool IsSamePrice(decimal? a, decimal b) =>
        a.HasValue && Math.Round(a.Value, 2, MidpointRounding.AwayFromZero) == Math.Round(b, 2, MidpointRounding.AwayFromZero);

    private static string BuildMoveKey(OrderState order) =>
        order.BrokerOrderId ?? order.ClientOrderId;

    // Wired from XAML — DOM price level click
    private void OnDomPriceLevelClicked(object sender, RoutedEventArgs e)
    {
        // handled via DomViewModel events
    }

    private void OnDomContextMenu(object sender, RoutedEventArgs e)
    {
        // DOM right-click context menu — handled by DomView
    }

    private async void OnHotButtonExecuted(object sender, HotButtonExecutedEventArgs e)
    {
        var symbol = ActiveDomSymbol();
        var summary = await _accountCache.GetAsync(_vm.SelectedAccountId);
        summary.Positions.TryGetValue(symbol, out var pos);
        await _vm.HotButtonsViewModel.ExecuteButtonAsync(
            e.Button,
            symbol,
            _vm.SelectedAccountId,
            _vm.ShareSize,
            _domService.CurrentQuote,
            pos);
    }

    private string ActiveDomSymbol() =>
        string.IsNullOrWhiteSpace(_vm.DomViewModel.Symbol)
            ? _vm.SelectedSymbol.Trim().ToUpperInvariant()
            : _vm.DomViewModel.Symbol.Trim().ToUpperInvariant();

    private void OrdersButton_Click(object sender, RoutedEventArgs e)
    {
        // Each order is stored under both ClientOrderId and BrokerOrderId keys; dedupe.
        var orders = _orderService.ActiveOrders.Values
            .Where(o => o.IsWorking)
            .DistinctBy(o => o.BrokerOrderId ?? o.ClientOrderId)
            .ToList();
        var dlg = new OrdersWindow(orders, _orderService, _vm.SelectedAccountId) { Owner = this };
        dlg.ShowDialog();
    }

    private void BookmapButton_Click(object sender, RoutedEventArgs e)
    {
        if (_bookmapDispatcher != null && _bookmapWindow != null)
        {
            _bookmapDispatcher.BeginInvoke(() =>
            {
                if (_bookmapWindow.WindowState == WindowState.Minimized)
                    _bookmapWindow.WindowState = WindowState.Normal;
                _bookmapWindow.Activate();
            });
            return;
        }

        var defaultHeight = ActualHeight;
        var defaultTop = Top;
        var defaultLeft = Left + ActualWidth + 8;
        _bookmapThread = new Thread(() =>
        {
            SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(Dispatcher.CurrentDispatcher));
            _bookmapDispatcher = Dispatcher.CurrentDispatcher;

            var vm = _services.GetRequiredService<DepthMapViewModel>();
            var window = new BookmapWindow(vm, defaultHeight, defaultTop, defaultLeft);
            _bookmapWindow = window;
            window.Closed += (_, _) =>
            {
                _bookmapWindow = null;
                _bookmapDispatcher = null;
                Dispatcher.CurrentDispatcher.BeginInvokeShutdown(DispatcherPriority.Background);
            };
            window.Show();

            Dispatcher.Run();
        })
        {
            Name = "FastDOM Bookmap UI",
            IsBackground = true
        };
        _bookmapThread.SetApartmentState(ApartmentState.STA);
        _bookmapThread.Start();
    }

    private void OpenOptionsChain_Click(object sender, RoutedEventArgs e)
    {
        var optVm = _services.GetRequiredService<OptionsChainViewModel>();
        optVm.Underlying = _vm.SelectedSymbol;
        optVm.LastPrice  = _domService.CurrentQuote?.Last ?? 0m;
        var dlg = new OptionsChainWindow(optVm) { Owner = this };
        dlg.Show();
    }

    protected override void OnClosed(EventArgs e)
    {
        CloseChartWindows();
        CloseBookmapWindow();
        _config.SaveAll();
        base.OnClosed(e);
    }

    private void CloseChartWindows()
    {
        Dispatcher? dispatcher;
        Thread? thread;
        lock (_chartThreadGate)
        {
            _chartShutdownRequested = true;
            _pendingCharts.Clear();
            dispatcher = _chartDispatcher;
            thread = _chartThread;
        }

        if (dispatcher != null)
        {
            try
            {
                dispatcher.Invoke(() =>
                {
                    foreach (var window in _chartWindows.ToArray()) window.Close();
                    Dispatcher.CurrentDispatcher.InvokeShutdown();
                });
            }
            catch (TaskCanceledException) { }
            catch (InvalidOperationException) { }
        }

        if (thread is { IsAlive: true }) thread.Join(TimeSpan.FromSeconds(2));
    }

    private void CloseBookmapWindow()
    {
        var dispatcher = _bookmapDispatcher;
        if (dispatcher == null) return;

        dispatcher.BeginInvoke(() =>
        {
            _bookmapWindow?.Close();
            Dispatcher.CurrentDispatcher.BeginInvokeShutdown(DispatcherPriority.Background);
        });
    }
}
