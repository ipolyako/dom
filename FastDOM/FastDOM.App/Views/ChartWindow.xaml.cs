using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using FastDOM.App.ViewModels;

namespace FastDOM.App.Views;

public partial class ChartWindow : Window
{
    private readonly ChartViewModel _viewModel;
    private bool _loaded;

    public ChartWindow(ChartViewModel viewModel, string symbol)
    {
        InitializeComponent(); DataContext = _viewModel = viewModel; _viewModel.Symbol = symbol;
        _viewModel.ChartChanged += OnChartChanged;
        Loaded += async (_, _) => { _loaded = true; ApplyIndicators(); await _viewModel.LoadAsync(); UpdateChart(true); };
    }
    private void OnChartChanged() => Dispatcher.BeginInvoke(() => UpdateChart(false));
    private void UpdateChart(bool reset) { Chart.SetData(_viewModel.Candles, reset); Chart.SetMarketDepth(_viewModel.CurrentDepth); }
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
    protected override void OnClosed(EventArgs e) { _viewModel.ChartChanged -= OnChartChanged; _viewModel.Dispose(); base.OnClosed(e); }
}
