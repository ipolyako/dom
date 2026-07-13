using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using FastDOM.App.Services;
using FastDOM.MarketData.Interfaces;

namespace FastDOM.App.Views;

public partial class FootprintWindow : Window
{
    private readonly FootprintService _footprints;
    private readonly IMarketDataClient _marketData;
    private readonly DispatcherTimer _timer;
    private string _symbol;
    private int _timeframe = 1;

    public FootprintWindow(FootprintService footprints, IMarketDataClient marketData, string symbol)
    {
        InitializeComponent();
        _footprints = footprints;
        _marketData = marketData;
        _symbol = symbol.Trim().ToUpperInvariant();
        SymbolBox.Text = _symbol;
        _timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(350) };
        _timer.Tick += (_, _) => RefreshChart();
        Loaded += async (_, _) =>
        {
            await _marketData.SubscribeQuotesAsync(_symbol);
            await _footprints.LoadHistoryAsync(_symbol);
            _timer.Start();
            RefreshChart();
        };
        Closed += async (_, _) =>
        {
            _timer.Stop();
            await _marketData.UnsubscribeQuotesAsync(_symbol);
        };
    }

    private async Task ChangeSymbolAsync(string symbol)
    {
        symbol = symbol.Trim().ToUpperInvariant();
        if (string.IsNullOrWhiteSpace(symbol) || symbol == _symbol) return;
        var previous = _symbol;
        _symbol = symbol;
        await _marketData.SubscribeQuotesAsync(_symbol);
        await _marketData.UnsubscribeQuotesAsync(previous);
        await _footprints.LoadHistoryAsync(_symbol);
        RefreshChart();
    }

    private void RefreshChart()
    {
        var levels = _footprints.Snapshot(_symbol, _timeframe, 14);
        Chart.Levels = levels;
        StatusText.Text = levels.Count == 0 ? "WAITING FOR TRADES" : $"{levels.Count:N0} LEVELS";
    }

    private void SymbolBox_GotKeyboardFocus(object sender, KeyboardFocusChangedEventArgs e) => SymbolBox.SelectAll();

    private void SymbolBox_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        var textBox = (TextBox)sender;
        if (!textBox.IsKeyboardFocusWithin)
        {
            e.Handled = true;
            textBox.Focus();
            textBox.SelectAll();
        }
    }

    private async void SymbolBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter) return;
        e.Handled = true;
        await ChangeSymbolAsync(SymbolBox.Text);
        SymbolBox.SelectAll();
    }

    private void TimeframeBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (TimeframeBox?.SelectedItem is ComboBoxItem item && int.TryParse(item.Tag?.ToString(), out var value))
            _timeframe = value;
        if (!IsLoaded) return;
        RefreshChart();
    }
}
