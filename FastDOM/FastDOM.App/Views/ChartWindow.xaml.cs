using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using FastDOM.App.Services;
using FastDOM.App.ViewModels;
using FastDOM.Core.Enums;
using FastDOM.Infrastructure.Config;

namespace FastDOM.App.Views;

public partial class ChartWindow : Window
{
    private readonly ChartViewModel _viewModel;
    private readonly HotkeyService _hotkeyService;
    private readonly ConfigManager _config;
    private readonly DispatcherTimer _renderTimer;
    private bool _loaded;
    private int _renderDirty;

    public ChartWindow(ChartViewModel viewModel, string symbol, string accountId, int quantity,
        HotButtonsViewModel hotButtons, HotkeyService hotkeyService, ConfigManager config,
        ChartWindowLayout? savedLayout = null)
    {
        InitializeComponent(); DataContext = _viewModel = viewModel; _hotkeyService = hotkeyService; _config = config;
        _viewModel.Symbol = symbol; _viewModel.ConfigureTrading(accountId, quantity, hotButtons);
        SideBox.ItemsSource = new[] { OrderSide.Buy, OrderSide.Sell }; SideBox.SelectedItem = OrderSide.Buy;
        TypeBox.ItemsSource = new[] { OrderType.Limit, OrderType.StopMarket, OrderType.StopLimit }; TypeBox.SelectedItem = OrderType.Limit;
        if (savedLayout == null)
            FitInitialWindowToContent();
        else
        {
            _viewModel.SelectedTimeframe = _viewModel.Timeframes.FirstOrDefault(x => x.Label == savedLayout.Timeframe) ?? _viewModel.SelectedTimeframe;
            _viewModel.IncludeExtendedHours = savedLayout.ExtendedHours;
            ApplyWindowLayout(savedLayout);
        }
        _viewModel.ChartChanged += OnChartChanged;
        Chart.PriceSelected += price => { _viewModel.StagePrice(price); UpdateChart(false); };
        Chart.OrderCancelRequested += async order => await _viewModel.CancelOrderAsync(order);
        Chart.OrderGroupCancelRequested += async orders => await _viewModel.CancelOrdersAsync(orders);
        Chart.OrderMoveRequested += async (orders, price) => await _viewModel.MoveOrdersAsync(orders, price);
        // Market data is ingested immediately by the view model. Painting is
        // capped at 30 FPS and runs below input priority so order clicks and
        // hotkeys are never queued behind a burst of quote/depth redraws.
        _renderTimer = new DispatcherTimer(DispatcherPriority.Background)
        {
            Interval = TimeSpan.FromMilliseconds(33)
        };
        _renderTimer.Tick += RenderTimer_Tick;
        Loaded += async (_, _) => { _loaded = true; _renderTimer.Start(); ApplyIndicators(); await _viewModel.LoadAsync(); UpdateChart(true); };
    }

    public ChartWindowLayout CaptureLayout()
    {
        var bounds = WindowState == WindowState.Maximized ? RestoreBounds : new Rect(Left, Top, Width, Height);
        return new ChartWindowLayout
        {
            Left = bounds.Left, Top = bounds.Top, Width = bounds.Width, Height = bounds.Height,
            Maximized = WindowState == WindowState.Maximized,
            Symbol = _viewModel.Symbol, Timeframe = _viewModel.SelectedTimeframe.Label,
            ExtendedHours = _viewModel.IncludeExtendedHours, AccountId = _viewModel.AccountId,
            Quantity = _viewModel.TradeQuantity
        };
    }

    private void ApplyWindowLayout(WorkspaceWindowLayout layout)
    {
        var virtualLeft = SystemParameters.VirtualScreenLeft;
        var virtualTop = SystemParameters.VirtualScreenTop;
        var virtualRight = virtualLeft + SystemParameters.VirtualScreenWidth;
        var virtualBottom = virtualTop + SystemParameters.VirtualScreenHeight;
        Width = Math.Clamp(layout.Width, MinWidth, Math.Max(MinWidth, SystemParameters.VirtualScreenWidth));
        Height = Math.Clamp(layout.Height, MinHeight, Math.Max(MinHeight, SystemParameters.VirtualScreenHeight));
        Left = Math.Clamp(layout.Left, virtualLeft, Math.Max(virtualLeft, virtualRight - Width));
        Top = Math.Clamp(layout.Top, virtualTop, Math.Max(virtualTop, virtualBottom - Height));
        WindowStartupLocation = WindowStartupLocation.Manual;
        if (layout.Maximized) WindowState = WindowState.Maximized;
    }

    private void FitInitialWindowToContent()
    {
        var unconstrained = new Size(double.PositiveInfinity, double.PositiveInfinity);
        TopToolbar.Measure(unconstrained);
        TradeToolbar.Measure(unconstrained);

        // Include border padding, window chrome, and a small safety allowance
        // for DPI/font rounding. Height remains proportional so the chart gets
        // useful plotting space instead of only fitting the toolbars.
        var naturalWidth = Math.Max(TopToolbar.DesiredSize.Width, TradeToolbar.DesiredSize.Width) + 42;
        var workArea = SystemParameters.WorkArea;
        var maxWidth = Math.Max(MinWidth, workArea.Width - 32);
        var maxHeight = Math.Max(MinHeight, workArea.Height - 32);
        Width = Math.Clamp(naturalWidth, MinWidth, maxWidth);
        Height = Math.Clamp(Width * 0.62, Math.Min(600, maxHeight), maxHeight);
    }

    private async void Window_KeyDown(object sender, KeyEventArgs e)
    {
        if (Keyboard.FocusedElement is TextBox && Keyboard.Modifiers == ModifierKeys.None)
            return;

        var gesture = HotkeyService.BuildGestureString(e);
        var button = _config.HotButtons.FirstOrDefault(b =>
            b.IsEnabled &&
            !string.IsNullOrWhiteSpace(b.KeyboardShortcut) &&
            string.Equals(b.KeyboardShortcut, gesture, StringComparison.OrdinalIgnoreCase));

        try
        {
            if (button != null)
            {
                e.Handled = true;
                await _viewModel.ExecuteHotButtonAsync(button);
                return;
            }

            var action = _hotkeyService.ProcessKeyDown(e);
            if (action != null)
            {
                e.Handled = true;
                await _viewModel.ExecuteHotkeyActionAsync(action);
            }
        }
        catch (Exception ex)
        {
            _viewModel.TradeStatus = $"Hotkey failed: {ex.Message}";
        }
    }

    private void OnChartChanged() => Interlocked.Exchange(ref _renderDirty, 1);

    private void RenderTimer_Tick(object? sender, EventArgs e)
    {
        if (Interlocked.Exchange(ref _renderDirty, 0) != 0)
            UpdateChart(false);
    }
    private void UpdateChart(bool reset) { Chart.SetData(_viewModel.Candles, reset); Chart.SetMarketDepth(_viewModel.CurrentDepth); Chart.SetTradingState(_viewModel.WorkingOrders, _viewModel.CurrentPosition, _viewModel.StagedPrice); }
    private async void SymbolBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter) return;
        var text = SymbolBox.Text?.Trim().ToUpperInvariant();
        if (string.IsNullOrWhiteSpace(text)) return;
        e.Handled = true;
        await _viewModel.LoadAsync(text);
        UpdateChart(true);
        // Match the main symbol box: leave the loaded symbol selected so the
        // next keystroke replaces it instead of appending to it.
        _ = Dispatcher.InvokeAsync(() =>
        {
            SymbolBox.Focus();
            SymbolBox.SelectAll();
        }, System.Windows.Threading.DispatcherPriority.Input);
    }

    private void SymbolBox_GotKeyboardFocus(object sender, KeyboardFocusChangedEventArgs e)
        => ((TextBox)sender).SelectAll();

    private void SymbolBox_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        var tb = (TextBox)sender;
        if (!tb.IsKeyboardFocusWithin)
        {
            e.Handled = true;
            tb.Focus();
            tb.SelectAll();
        }
    }
    private async void Timeframe_Changed(object sender, SelectionChangedEventArgs e) { if(!_loaded)return;await _viewModel.LoadAsync();UpdateChart(true); }
    private async void Extended_Click(object sender, RoutedEventArgs e) { if(!_loaded)return;await _viewModel.LoadAsync();UpdateChart(true); }
    private async void Refresh_Click(object sender, RoutedEventArgs e) { await _viewModel.LoadAsync();UpdateChart(false); }
    private void Latest_Click(object sender, RoutedEventArgs e) => Chart.ResetView();
    private void Indicator_Click(object sender, RoutedEventArgs e) => ApplyIndicators();
    private void ApplyIndicators()
    {
        Chart.ShowEma9 = Ema9Toggle.IsChecked == true; Chart.ShowEma20 = Ema20Toggle.IsChecked == true;
        Chart.ShowVwap = VwapToggle.IsChecked == true; Chart.ShowLiquidity = LiquidityToggle.IsChecked == true;
        Chart.ShowPriorSession = PriorToggle.IsChecked == true; Chart.ShowPremarket = PremarketToggle.IsChecked == true;
        Chart.ShowCamarilla = CamarillaToggle.IsChecked == true; Chart.InvalidateVisual();
    }
    private void TradeInput_Changed(object sender, SelectionChangedEventArgs e) { if (SideBox?.SelectedItem is OrderSide side) _viewModel.TradeSide = side; if (TypeBox?.SelectedItem is OrderType type) _viewModel.TradeOrderType = type; }
    private async void Place_Click(object sender, RoutedEventArgs e) => await ShowResultAsync(() => _viewModel.SubmitAsync());
    private async void BuyMarket_Click(object sender, RoutedEventArgs e) { SideBox.SelectedItem = OrderSide.Buy; _viewModel.TradeSide = OrderSide.Buy; await ShowResultAsync(() => _viewModel.SubmitAsync(true)); }
    private async void SellMarket_Click(object sender, RoutedEventArgs e) { SideBox.SelectedItem = OrderSide.Sell; _viewModel.TradeSide = OrderSide.Sell; await ShowResultAsync(() => _viewModel.SubmitAsync(true)); }
    private async void CancelAll_Click(object sender, RoutedEventArgs e) { await _viewModel.CancelAllAsync(); UpdateChart(false); }
    private async void RiskBuy_Click(object sender, RoutedEventArgs e) => await _viewModel.ExecuteConfiguredButtonAsync("risk_buy_5t");
    private async void RiskBuySimple_Click(object sender, RoutedEventArgs e) => await _viewModel.ExecuteConfiguredButtonAsync("risk_buy_simple");
    private async void Secure_Click(object sender, RoutedEventArgs e) => await _viewModel.ExecuteConfiguredButtonAsync("secure_position");
    private async Task ShowResultAsync(Func<Task<(bool ok, string message)>> action) { var result = await action(); if (!result.ok) MessageBox.Show(this, result.message, "Chart order", MessageBoxButton.OK, MessageBoxImage.Warning); UpdateChart(false); }
    protected override void OnClosed(EventArgs e) { _renderTimer.Stop(); _renderTimer.Tick -= RenderTimer_Tick; _viewModel.ChartChanged -= OnChartChanged; _viewModel.Dispose(); base.OnClosed(e); }
}
