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
        Loaded += async (_, _) => { _loaded = true; await _viewModel.LoadAsync(); Chart.SetData(_viewModel.Candles, true); };
    }
    private void OnChartChanged() => Dispatcher.BeginInvoke(() => Chart.SetData(_viewModel.Candles));
    private async void SymbolBox_KeyDown(object sender, KeyEventArgs e) { if(e.Key!=Key.Enter)return;e.Handled=true;await _viewModel.LoadAsync(SymbolBox.Text);Chart.SetData(_viewModel.Candles,true);SymbolBox.SelectAll(); }
    private void SymbolBox_GotKeyboardFocus(object sender, KeyboardFocusChangedEventArgs e) => SymbolBox.SelectAll();
    private void SymbolBox_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e) { if(SymbolBox.IsKeyboardFocusWithin)return;e.Handled=true;SymbolBox.Focus();SymbolBox.SelectAll(); }
    private async void Timeframe_Changed(object sender, SelectionChangedEventArgs e) { if(!_loaded)return;await _viewModel.LoadAsync();Chart.SetData(_viewModel.Candles,true); }
    private async void Extended_Click(object sender, RoutedEventArgs e) { if(!_loaded)return;await _viewModel.LoadAsync();Chart.SetData(_viewModel.Candles,true); }
    private async void Refresh_Click(object sender, RoutedEventArgs e) { await _viewModel.LoadAsync();Chart.SetData(_viewModel.Candles); }
    private void Latest_Click(object sender, RoutedEventArgs e) => Chart.ResetView();
    protected override void OnClosed(EventArgs e) { _viewModel.ChartChanged -= OnChartChanged; _viewModel.Dispose(); base.OnClosed(e); }
}
