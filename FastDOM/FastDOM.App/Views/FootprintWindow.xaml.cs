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
    private bool _initialViewportPending = true;
    private int _lastBarCount;

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
        _initialViewportPending = true;
        _lastBarCount = 0;
        await _marketData.SubscribeQuotesAsync(_symbol);
        await _marketData.UnsubscribeQuotesAsync(previous);
        await _footprints.LoadHistoryAsync(_symbol);
        RefreshChart();
    }

    private void RefreshChart()
    {
        var levels = _footprints.Snapshot(_symbol, _timeframe, 240);
        var barCount = levels.Select(x => x.BarTimeUtc).Distinct().Count();
        var wasAtLatest = ChartScroller.ScrollableWidth <= 1
            || ChartScroller.HorizontalOffset >= ChartScroller.ScrollableWidth - 24;
        Chart.Levels = levels;
        StatusText.Text = levels.Count == 0 ? "WAITING FOR TRADES" : $"{levels.Count:N0} LEVELS";
        if (levels.Count > 0 && (_initialViewportPending || (barCount != _lastBarCount && wasAtLatest)))
        {
            _initialViewportPending = false;
            Dispatcher.BeginInvoke(() =>
            {
                ChartScroller.ScrollToRightEnd();
                var target = Math.Max(0, Chart.LatestAnchorY - ChartScroller.ViewportHeight / 2);
                ChartScroller.ScrollToVerticalOffset(target);
            }, DispatcherPriority.Loaded);
        }
        _lastBarCount = barCount;
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
        _initialViewportPending = true;
        RefreshChart();
    }

    private void ZoomIn_Click(object sender, RoutedEventArgs e) => SetZoom(Chart.Zoom + 0.15);

    private void ZoomOut_Click(object sender, RoutedEventArgs e) => SetZoom(Chart.Zoom - 0.15);

    private void ChartScroller_PreviewMouseWheel(object sender, MouseWheelEventArgs e)
    {
        if ((Keyboard.Modifiers & ModifierKeys.Control) == 0) return;
        e.Handled = true;
        SetZoom(Chart.Zoom + (e.Delta > 0 ? 0.1 : -0.1));
    }

    private void SetZoom(double zoom)
    {
        var centerX = ChartScroller.HorizontalOffset + ChartScroller.ViewportWidth / 2;
        var centerY = ChartScroller.VerticalOffset + ChartScroller.ViewportHeight / 2;
        var prior = Chart.Zoom;
        Chart.Zoom = zoom;
        ZoomText.Text = $"{Chart.Zoom:P0}";
        var scale = Chart.Zoom / prior;
        Dispatcher.BeginInvoke(() =>
        {
            ChartScroller.ScrollToHorizontalOffset(centerX * scale - ChartScroller.ViewportWidth / 2);
            ChartScroller.ScrollToVerticalOffset(centerY * scale - ChartScroller.ViewportHeight / 2);
        }, DispatcherPriority.Loaded);
    }
}
