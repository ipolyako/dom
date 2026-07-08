using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
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
    private readonly IServiceProvider _services;
    private bool _killSwitchPending;

    public MainWindow(
        MainViewModel vm,
        OrderService orderService,
        HotkeyService hotkeyService,
        AuditLogger audit,
        ConfigManager config,
        IBrokerClient broker,
        DomService domService,
        IServiceProvider services)
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

        // Auto-scroll activity log — defer to Background priority so the ItemsControl
        // finishes processing CollectionChanged before ScrollIntoView triggers layout.
        _vm.ActivityLog.CollectionChanged += (_, _) =>
            Dispatcher.InvokeAsync(
                () => { if (ActivityListBox.Items.Count > 0) ActivityListBox.ScrollIntoView(ActivityListBox.Items[ActivityListBox.Items.Count - 1]); },
                System.Windows.Threading.DispatcherPriority.Background);

        // Wire DOM click events
        _vm.DomViewModel.PriceLevelClicked += OnDomPriceLevelClickedInternal;
        _vm.DomViewModel.OrderMoveRequested += OnOrderMoveRequested;

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
        var btn = _config.HotButtons.FirstOrDefault(b =>
            b.IsEnabled &&
            !string.IsNullOrWhiteSpace(b.KeyboardShortcut) &&
            string.Equals(b.KeyboardShortcut, gesture, StringComparison.OrdinalIgnoreCase));
        if (btn != null)
        {
            e.Handled = true;
            _ = Task.Run(async () =>
            {
                var summary = await _broker.GetAccountSummaryAsync(_vm.SelectedAccountId);
                summary.Positions.TryGetValue(_vm.SelectedSymbol, out var pos);
                await _vm.HotButtonsViewModel.ExecuteButtonAsync(
                    btn, _vm.SelectedSymbol, _vm.SelectedAccountId,
                    _vm.ShareSize, _domService.CurrentQuote, pos);
            });
            return;
        }

        // 2. Fixed hotkey bindings (HotkeyConfig)
        // Skip if focus is on a text field and no modifier is held — those are text input,
        // not hotkeys. Modifier combos (Ctrl+F, Ctrl+Shift+E) always fire.
        if (Keyboard.FocusedElement is TextBox && Keyboard.Modifiers == ModifierKeys.None)
            return;

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

    private void HotkeyIndicator_Click(object sender, MouseButtonEventArgs e)
        => _vm.ToggleHotkeysCommand.Execute(null);

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
        var summary = await _broker.GetAccountSummaryAsync(_vm.SelectedAccountId);
        summary.Positions.TryGetValue(_vm.SelectedSymbol, out var pos);
        await _vm.HotButtonsViewModel.ExecuteActionAsync(
            e.Action, _vm.SelectedSymbol, _vm.SelectedAccountId, _vm.ShareSize, _domService.CurrentQuote, pos);
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

        await _audit.LogKillSwitchAsync(_vm.SelectedAccountId, _vm.SelectedSymbol, "CancelAll+Flatten");

        await _orderService.CancelAllForSymbolAsync(_vm.SelectedAccountId, _vm.SelectedSymbol);

        var summary = await _broker.GetAccountSummaryAsync(_vm.SelectedAccountId);
        if (summary.Positions.TryGetValue(_vm.SelectedSymbol, out var pos) && !pos.IsFlat)
        {
            var side = pos.Side == PositionSide.Long ? OrderSide.Sell : OrderSide.Buy;
            var req = new OrderRequest
            {
                AccountId = _vm.SelectedAccountId,
                Symbol    = _vm.SelectedSymbol,
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
        // Populate ticket for visibility
        _vm.OrderTicketViewModel.PopulateFromDomClick(price, side, orderType);

        // One-click order submission — standard DOM behavior
        var req = new OrderRequest
        {
            AccountId   = _vm.SelectedAccountId,
            Symbol      = _vm.SelectedSymbol,
            Side        = side,
            Quantity    = _vm.ShareSize,
            OrderType   = orderType,
            LimitPrice  = orderType is OrderType.Limit or OrderType.StopLimit or OrderType.MarketableLimit
                          ? price : null,
            StopPrice   = orderType is OrderType.StopMarket or OrderType.StopLimit ? price : null,
            Source      = OrderSource.DomClick
        };

        var account = await _broker.GetAccountSummaryAsync(_vm.SelectedAccountId);
        var (success, msg) = await _orderService.SubmitOrderAsync(req, account, _domService.CurrentQuote);
        _vm.LastToast = success
            ? $"{side} {_vm.ShareSize} {_vm.SelectedSymbol} @ {price:F2} SENT"
            : $"REJECTED: {msg}";
    }

    private async void OnOrderMoveRequested(OrderState order, decimal newPrice)
    {
        if (string.IsNullOrEmpty(order.BrokerOrderId)) return;
        var replacement = new OrderReplace
        {
            OriginalClientOrderId = order.ClientOrderId,
            BrokerOrderId = order.BrokerOrderId,
            NewLimitPrice = newPrice,
            Source = OrderSource.DomClick
        };
        var (ok, msg) = await _orderService.ReplaceOrderAsync(_vm.SelectedAccountId, replacement);
        _vm.LastToast = ok ? $"Order moved to {newPrice:F2}" : $"Move failed: {msg}";
    }

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
        var summary = await _broker.GetAccountSummaryAsync(_vm.SelectedAccountId);
        summary.Positions.TryGetValue(_vm.SelectedSymbol, out var pos);
        await _vm.HotButtonsViewModel.ExecuteButtonAsync(
            e.Button,
            _vm.SelectedSymbol,
            _vm.SelectedAccountId,
            _vm.ShareSize,
            _vm.DomViewModel.Rows.FirstOrDefault() != null ? null : null,
            pos);
    }

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
        _config.SaveAll();
        base.OnClosed(e);
    }
}
