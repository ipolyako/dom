using System.Globalization;
using System.Windows;
using System.Windows.Media;
using FastDOM.App.Services;

namespace FastDOM.App.Views;

public sealed class FootprintChartControl : FrameworkElement
{
    private const double BaseRowHeight = 18;
    private const double BaseCandleWidth = 116;
    private const double CandleGap = 18;
    private const double Header = 36;
    private const double Footer = 38;
    private const double Left = 78;
    private const double Right = 60;
    private const double LabelInset = 6;
    private double _zoom = 1.0;
    private IReadOnlyList<FootprintLevel> _levels = [];
    public IReadOnlyList<FootprintLevel> Levels
    {
        get => _levels;
        set { _levels = value ?? []; InvalidateMeasure(); InvalidateVisual(); }
    }

    public double Zoom
    {
        get => _zoom;
        set
        {
            var zoom = Math.Clamp(value, 0.5, 2.5);
            if (Math.Abs(_zoom - zoom) < 0.001) return;
            _zoom = zoom;
            InvalidateMeasure();
            InvalidateVisual();
        }
    }

    public double LatestAnchorY
    {
        get
        {
            if (Levels.Count == 0) return 0;
            var prices = Levels.Select(x => x.Price).Distinct().OrderByDescending(x => x).ToArray();
            var latestBar = Levels.Max(x => x.BarTimeUtc);
            var anchor = Levels.Where(x => x.BarTimeUtc == latestBar)
                .OrderByDescending(x => x.TotalVolume).First().Price;
            var row = Array.IndexOf(prices, anchor);
            return Header + (row + 0.5) * BaseRowHeight * Zoom;
        }
    }

    protected override Size MeasureOverride(Size availableSize)
    {
        var barCount = Math.Max(1, Levels.Select(x => x.BarTimeUtc).Distinct().Count());
        var priceCount = Math.Max(8, Levels.Select(x => x.Price).Distinct().Count());
        var width = Left + barCount * (BaseCandleWidth * Zoom + CandleGap) + Right;
        var height = Header + priceCount * BaseRowHeight * Zoom + Footer;
        return new Size(Math.Max(720, width), Math.Max(420, height));
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
        var priceIndex = prices.Select((p, i) => (p, i)).ToDictionary(x => x.p, x => x.i);
        var rowHeight = BaseRowHeight * Zoom;
        var candleWidth = BaseCandleWidth * Zoom;
        var slotWidth = candleWidth + CandleGap;
        var plotRight = ActualWidth - Right;
        var startX = Left;

        DrawText(dc, "PRICE", 6, 9, Brushes.Gray, 9);
        DrawText(dc, "PRICE", plotRight + LabelInset, 9, Brushes.Gray, 9);
        DrawText(dc, $"BID × ASK    TIME ({TimeZoneInfo.Local.StandardName})", startX + 6, 9, Brushes.Gray, 9);

        for (var i = 0; i < prices.Length; i++)
        {
            var y = Header + i * rowHeight;
            dc.DrawLine(new Pen(Frozen("#1C2930"), 0.7),
                new Point(Left, y), new Point(plotRight, y));
            var label = FormatPrice(prices[i]);
            DrawText(dc, label, 4, y + 1, Brushes.LightGray, 10);
            DrawTextRight(dc, label, plotRight + LabelInset, y + 1, Brushes.LightGray, 10);
        }
        dc.DrawLine(new Pen(Frozen("#3F4C56"), 1), new Point(Left, Header), new Point(Left, Header + prices.Length * rowHeight));
        dc.DrawLine(new Pen(Frozen("#3F4C56"), 1), new Point(plotRight, Header), new Point(plotRight, Header + prices.Length * rowHeight));

        // A real aligned summary row, rather than floating delta labels.
        var deltaY = Header + prices.Length * rowHeight + 3;
        dc.DrawRectangle(Frozen("#10171B"), new Pen(Frozen("#34434B"), 1),
            new Rect(0, deltaY, ActualWidth, Footer - 3));
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
                new Point(x - CandleGap / 2, Header), new Point(x - CandleGap / 2, deltaY));

            if (visible.Length > 0)
            {
                var firstRow = visible.Min(level => priceIndex[level.Price]);
                var lastRow = visible.Max(level => priceIndex[level.Price]);
                dc.DrawRectangle(null, new Pen(outline, 1.4), new Rect(
                    x, Header + firstRow * rowHeight,
                    candleWidth, Math.Max(rowHeight, (lastRow - firstRow + 1) * rowHeight)));
            }

            foreach (var level in bar)
            {
                if (!priceIndex.TryGetValue(level.Price, out var row)) continue;
                var y = Header + row * rowHeight;
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

            var deltaCell = new Rect(x, deltaY + 2, candleWidth, Footer - 7);
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

    private static void DrawTextRight(DrawingContext dc, string text, double x, double y, Brush brush, double size)
    {
        var formatted = new FormattedText(text, CultureInfo.CurrentCulture, FlowDirection.LeftToRight,
            new Typeface("Consolas"), size, brush, 1.0);
        dc.DrawText(formatted, new Point(x + (50 - formatted.Width), y));
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
