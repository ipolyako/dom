using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using FastDOM.App.ViewModels;
using FastDOM.Core.Enums;

namespace FastDOM.App.Views;

public partial class ChartWindow : Window
{
    private readonly ChartViewModel _viewModel;
    private bool _loaded;
    private int _renderQueued;

    public ChartWindow(ChartViewModel viewModel, string symbol, string accountId, int quantity)
    {
        InitializeComponent(); DataContext = _viewModel = viewModel; _viewModel.Symbol = symbol; _viewModel.ConfigureTrading(accountId, quantity);
        SideBox.ItemsSource = new[] { OrderSide.Buy, OrderSide.Sell }; SideBox.SelectedItem = OrderSide.Buy;
        TypeBox.ItemsSource = new[] { OrderType.Limit, OrderType.StopMarket, OrderType.StopLimit }; TypeBox.SelectedItem = OrderType.Limit;
        _viewModel.ChartChanged += OnChartChanged;
        Chart.PriceSelected += price => { _viewModel.StagePrice(price); UpdateChart(false); };
        Chart.OrderCancelRequested += async order => await _viewModel.CancelOrderAsync(order);
        Chart.OrderGroupCancelRequested += async orders => await _viewModel.CancelOrdersAsync(orders);
        Loaded += async (_, _) => { _loaded = true; ApplyIndicators(); await _viewModel.LoadAsync(); UpdateChart(true); };
    }
    private void OnChartChanged()
    {
        if (Interlocked.Exchange(ref _renderQueued, 1) != 0) return;
        Dispatcher.BeginInvoke(() =>
        {
            Interlocked.Exchange(ref _renderQueued, 0);
            UpdateChart(false);
        }, System.Windows.Threading.DispatcherPriority.Render);
    }
    private void UpdateChart(bool reset) { Chart.SetData(_viewModel.Candles, reset); Chart.SetMarketDepth(_viewModel.CurrentDepth); Chart.SetTradingState(_viewModel.WorkingOrders, _viewModel.CurrentPosition, _viewModel.StagedPrice); }
    private async void SymbolBox_KeyDown(object sender, KeyEventArgs e) { if(e.Key!=Key.Enter)return;e.Handled=true;await _viewModel.LoadAsync(SymbolBox.Text);UpdateChart(true);SymbolBox.SelectAll(); }
    private void SymbolBox_GotKeyboardFocus(object sender, KeyboardFocusChangedEventArgs e) => SymbolBox.SelectAll();
    private void SymbolBox_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e) { if(SymbolBox.IsKeyboardFocusWithin)return;e.Handled=true;SymbolBox.Focus();SymbolBox.SelectAll(); }
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
    private void Arm_Click(object sender, RoutedEventArgs e)
    {
        _viewModel.TradingArmed = ArmToggle.IsChecked == true;
        ArmToggle.Content = _viewModel.TradingArmed ? "TRADE ARMED" : "TRADE DISARMED";
        ArmToggle.Background = _viewModel.TradingArmed ? System.Windows.Media.Brushes.Red : System.Windows.Media.Brushes.DarkSlateGray;
        ArmToggle.Foreground = System.Windows.Media.Brushes.White;
        _viewModel.TradeStatus = _viewModel.TradingArmed ? "Double-click chart to stage a price" : "Chart trading disarmed";
    }
    private void TradeInput_Changed(object sender, SelectionChangedEventArgs e) { if (SideBox?.SelectedItem is OrderSide side) _viewModel.TradeSide = side; if (TypeBox?.SelectedItem is OrderType type) _viewModel.TradeOrderType = type; }
    private async void Place_Click(object sender, RoutedEventArgs e) => await ShowResultAsync(() => _viewModel.SubmitAsync());
    private async void BuyMarket_Click(object sender, RoutedEventArgs e) { SideBox.SelectedItem = OrderSide.Buy; _viewModel.TradeSide = OrderSide.Buy; await ShowResultAsync(() => _viewModel.SubmitAsync(true)); }
    private async void SellMarket_Click(object sender, RoutedEventArgs e) { SideBox.SelectedItem = OrderSide.Sell; _viewModel.TradeSide = OrderSide.Sell; await ShowResultAsync(() => _viewModel.SubmitAsync(true)); }
    private async void CancelAll_Click(object sender, RoutedEventArgs e) { await _viewModel.CancelAllAsync(); UpdateChart(false); }
    private async Task ShowResultAsync(Func<Task<(bool ok, string message)>> action) { var result = await action(); if (!result.ok) MessageBox.Show(this, result.message, "Chart order", MessageBoxButton.OK, MessageBoxImage.Warning); UpdateChart(false); }
    protected override void OnClosed(EventArgs e) { _viewModel.ChartChanged -= OnChartChanged; _viewModel.Dispose(); base.OnClosed(e); }
}
