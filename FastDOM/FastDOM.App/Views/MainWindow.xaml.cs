using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using FastDOM.App.Services;
using FastDOM.App.ViewModels;
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
    private bool _killSwitchPending;
    private bool _symbolSelectionUpdating;

    public MainWindow(
        MainViewModel vm,
        OrderService orderService,
        HotkeyService hotkeyService,
        AuditLogger audit,
        ConfigManager config,
        IBrokerClient broker,
        DomService domService)
    {
        InitializeComponent();
        DataContext = _vm = vm;
        _orderService = orderService;
        _hotkeyService = hotkeyService;
        _audit = audit;
        _config = config;
        _broker = broker;
        _domService = domService;

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
    }

    protected override async void OnContentRendered(EventArgs e)
    {
        base.OnContentRendered(e);
        await _vm.ConnectCommand.ExecuteAsync(null);
    }

    private void Window_KeyDown(object sender, KeyEventArgs e)
    {
        bool inTextBox = FocusManager.GetFocusedElement(this) is TextBox;
        var action = _hotkeyService.ProcessKeyDown(e, inTextBox);
        if (action != null)
        {
            e.Handled = true;
            _ = _vm.ExecuteHotkeyActionAsync(action);
        }
    }

    private async void SymbolComboBox_SelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
    {
        if (_vm == null || _symbolSelectionUpdating) return;
        try
        {
            if (e.AddedItems.Count > 0 && e.AddedItems[0] is string sym)
            {
                _symbolSelectionUpdating = true;
                _vm.SelectedSymbol = sym;
                _symbolSelectionUpdating = false;

                if (_vm.ChangeSymbolCommand.CanExecute(null))
                    await _vm.ChangeSymbolCommand.ExecuteAsync(null);
            }
        }
        catch (Exception ex)
        {
            _symbolSelectionUpdating = false;
            _vm.LastToast = $"Symbol change error: {ex.Message}";
        }
    }

    private async void SymbolComboBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter && !string.IsNullOrWhiteSpace(_vm.SelectedSymbol))
        {
            _vm.SelectedSymbol = _vm.SelectedSymbol.Trim().ToUpperInvariant();
            await _vm.ChangeSymbolCommand.ExecuteAsync(null);
            SymbolComboBox.MoveFocus(new System.Windows.Input.TraversalRequest(System.Windows.Input.FocusNavigationDirection.Next));
        }
    }

    private void HotkeyIndicator_Click(object sender, MouseButtonEventArgs e)
        => _vm.ToggleHotkeysCommand.Execute(null);

    private void HotkeySettings_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new HotkeySettingsWindow(_config.HotkeyConfig, _config) { Owner = this };
        dlg.ShowDialog();
    }

    private void SizePreset_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button btn && int.TryParse(btn.Tag?.ToString(), out var size))
            _vm.ShareSize = size;
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

    protected override void OnClosed(EventArgs e)
    {
        _config.SaveAll();
        base.OnClosed(e);
    }
}
