using System.Globalization;
using System.Windows;
using System.Windows.Media;
using FastDOM.App.Services;

namespace FastDOM.App.Views;

public sealed class FootprintChartControl : FrameworkElement
{
    private IReadOnlyList<FootprintLevel> _levels = [];
    public IReadOnlyList<FootprintLevel> Levels
    {
        get => _levels;
        set { _levels = value ?? []; InvalidateVisual(); }
    }

    protected override void OnRender(DrawingContext dc)
    {
        base.OnRender(dc);
        dc.DrawRectangle(new SolidColorBrush(Color.FromRgb(8, 12, 15)), null, new Rect(RenderSize));
        if (Levels.Count == 0)
        {
            DrawText(dc, "Waiting for live trades / loading Derby history…", 16, 18, Brushes.Gray, 13);
            return;
        }

        var bars = Levels.GroupBy(x => x.BarTimeUtc).OrderBy(x => x.Key).ToArray();
        var prices = Levels.Select(x => x.Price).Distinct().OrderByDescending(x => x).ToArray();
        const double header = 36, footer = 38, left = 78, right = 8;
        var availableHeight = Math.Max(100, ActualHeight - header - footer);
        var maxRows = Math.Max(8, (int)(availableHeight / 16));
        if (prices.Length > maxRows)
        {
            var anchor = bars[^1].OrderByDescending(x => x.TotalVolume).First().Price;
            prices = prices.OrderBy(x => Math.Abs(x - anchor)).Take(maxRows).OrderByDescending(x => x).ToArray();
        }
        var priceIndex = prices.Select((p, i) => (p, i)).ToDictionary(x => x.p, x => x.i);
        var rowHeight = availableHeight / prices.Length;
        const double candleWidth = 116;
        const double candleGap = 18;
        var slotWidth = candleWidth + candleGap;
        var plotRight = ActualWidth - right;
        var totalWidth = bars.Length * slotWidth;
        var startX = Math.Max(left, plotRight - totalWidth);

        DrawText(dc, "PRICE", 6, 9, Brushes.Gray, 9);
        DrawText(dc, "BID × ASK", startX + 6, 9, Brushes.Gray, 9);

        for (var i = 0; i < prices.Length; i++)
        {
            var y = header + i * rowHeight;
            dc.DrawLine(new Pen(Frozen("#1C2930"), 0.7),
                new Point(left, y), new Point(plotRight, y));
            DrawText(dc, FormatPrice(prices[i]), 4, y + 1, Brushes.LightGray, 10);
        }

        // A real aligned summary row, rather than floating delta labels.
        var deltaY = ActualHeight - footer + 3;
        dc.DrawRectangle(Frozen("#10171B"), new Pen(Frozen("#34434B"), 1),
            new Rect(0, deltaY, ActualWidth, footer - 3));
        DrawText(dc, "DELTA", 10, deltaY + 9, Brushes.LightGray, 10);

        for (var b = 0; b < bars.Length; b++)
        {
            var x = startX + b * slotWidth;
            var bar = bars[b].ToArray();
            var bid = bar.Sum(level => level.BidVolume);
            var ask = bar.Sum(level => level.AskVolume);
            var delta = ask - bid;
            var outline = delta >= 0 ? Frozen("#00C853") : Frozen("#FF3D00");
            var visible = bar.Where(level => priceIndex.ContainsKey(level.Price)).ToArray();
            var maxBarVolume = Math.Max(1L, bar.Max(level => level.TotalVolume));
            var poc = bar.OrderByDescending(level => level.TotalVolume).First();

            DrawCenteredText(dc, bars[b].Key.ToLocalTime().ToString("HH:mm"),
                x, candleWidth, 9, Brushes.LightGray, 10);
            dc.DrawLine(new Pen(Frozen("#243239"), 0.8),
                new Point(x - candleGap / 2, header), new Point(x - candleGap / 2, deltaY));

            if (visible.Length > 0)
            {
                var firstRow = visible.Min(level => priceIndex[level.Price]);
                var lastRow = visible.Max(level => priceIndex[level.Price]);
                dc.DrawRectangle(null, new Pen(outline, 1.4), new Rect(
                    x, header + firstRow * rowHeight,
                    candleWidth, Math.Max(rowHeight, (lastRow - firstRow + 1) * rowHeight)));
            }

            foreach (var level in bar)
            {
                if (!priceIndex.TryGetValue(level.Price, out var row)) continue;
                var y = header + row * rowHeight;
                var intensity = (byte)Math.Clamp(24 + 125.0 * level.TotalVolume / maxBarVolume, 24, 149);
                var color = level.Delta >= 0
                    ? Color.FromArgb(intensity, 0, 96, 82)
                    : Color.FromArgb(intensity, 132, 24, 24);
                var cell = new Rect(x + 1, y + 1, candleWidth - 2, Math.Max(1, rowHeight - 2));
                var isPoc = level.Price == poc.Price;
                dc.DrawRectangle(new SolidColorBrush(color),
                    isPoc ? new Pen(Frozen("#FFD600"), 1.3) : null, cell);
                var text = $"{level.BidVolume:N0} × {level.AskVolume:N0}";
                var imbalanceBrush = level.AskVolume >= Math.Max(3, level.BidVolume * 3)
                    ? Frozen("#69F0AE")
                    : level.BidVolume >= Math.Max(3, level.AskVolume * 3)
                        ? Frozen("#FF8A80") : Brushes.White;
                DrawCenteredText(dc, text, x, candleWidth,
                    y + Math.Max(0, (rowHeight - 12) / 2), imbalanceBrush, 10);
                if (level.UnknownVolume > 0)
                    DrawText(dc, $"+{level.UnknownVolume:N0}?", x + candleWidth - 35, y + 1, Brushes.Gold, 7);
            }

            var deltaCell = new Rect(x, deltaY + 2, candleWidth, footer - 7);
            dc.DrawRectangle(delta >= 0 ? Frozen("#103525") : Frozen("#3A1717"),
                new Pen(outline, 0.8), deltaCell);
            DrawCenteredText(dc, delta.ToString("+#,0;-#,0;0"), x, candleWidth,
                deltaY + 9, delta >= 0 ? Frozen("#69F0AE") : Frozen("#FF8A80"), 11);
        }
    }

    private static string FormatPrice(decimal value) => value < 10 ? value.ToString("0.0000") : value.ToString("0.00");

    private static void DrawText(DrawingContext dc, string text, double x, double y, Brush brush, double size)
    {
        var formatted = new FormattedText(text, CultureInfo.CurrentCulture, FlowDirection.LeftToRight,
            new Typeface("Consolas"), size, brush, 1.0);
        dc.DrawText(formatted, new Point(x, y));
    }

    private static void DrawCenteredText(DrawingContext dc, string text, double x, double width,
        double y, Brush brush, double size)
    {
        var formatted = new FormattedText(text, CultureInfo.CurrentCulture, FlowDirection.LeftToRight,
            new Typeface("Consolas"), size, brush, 1.0);
        dc.DrawText(formatted, new Point(x + Math.Max(2, (width - formatted.Width) / 2), y));
    }

    private static Brush Frozen(string hex)
    {
        var brush = new SolidColorBrush((Color)ColorConverter.ConvertFromString(hex));
        brush.Freeze();
        return brush;
    }
}
