using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using FastDOM.MarketData.Models;
using FastDOM.Core.Models;
using FastDOM.Core.Enums;

namespace FastDOM.App.Views;

public class TradingChartControl : FrameworkElement
{
    private IReadOnlyList<PriceCandle> _candles = [];
    private MarketDepth? _depth;
    private IReadOnlyList<OrderState> _orders = [];
    private Position? _position;
    private decimal? _stagedPrice;
    private decimal _renderMin, _renderMax;
    private double _renderTop, _renderPriceBottom, _renderPlotRight;
    public event Action<decimal>? PriceSelected;
    public event Action<OrderState>? OrderCancelRequested;
    private int _visibleCount = 100;
    private int _barsBack;
    private Point? _crosshair;
    private Point? _dragStart;
    private int _dragBarsBack;
    private readonly Typeface _font = new("Consolas");
    private static readonly Brush Up = Frozen("#26A69A"), Down = Frozen("#EF5350"), Grid = Frozen("#25313A");
    private static readonly Pen Ema9Pen = new(Frozen("#FFD54F"), 1.4), Ema20Pen = new(Frozen("#42A5F5"), 1.4), VwapPen = new(Frozen("#CE93D8"), 1.3);
    public bool ShowEma9 { get; set; } = true;
    public bool ShowEma20 { get; set; } = true;
    public bool ShowVwap { get; set; } = true;
    public bool ShowLiquidity { get; set; }
    public bool ShowPriorSession { get; set; }
    public bool ShowPremarket { get; set; }
    public bool ShowCamarilla { get; set; }

    public void SetData(IReadOnlyList<PriceCandle> candles, bool resetView = false)
    {
        _candles = candles;
        if (resetView) { _barsBack = 0; _visibleCount = Math.Clamp(candles.Count, 40, 120); }
        InvalidateVisual();
    }

    public void SetMarketDepth(MarketDepth? depth) { _depth = depth; InvalidateVisual(); }
    public void SetTradingState(IReadOnlyList<OrderState> orders, Position? position, decimal? stagedPrice) { _orders = orders; _position = position; _stagedPrice = stagedPrice; InvalidateVisual(); }

    public void ResetView() { _barsBack = 0; _visibleCount = Math.Clamp(_candles.Count, 40, 120); InvalidateVisual(); }

    protected override void OnMouseWheel(MouseWheelEventArgs e)
    {
        _visibleCount = Math.Clamp(_visibleCount + (e.Delta < 0 ? 15 : -15), 20, Math.Max(20, Math.Min(500, _candles.Count)));
        InvalidateVisual(); e.Handled = true;
    }
    protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        var point = e.GetPosition(this);
        if (e.ClickCount >= 2 && TryPrice(point, out var price)) { PriceSelected?.Invoke(price); e.Handled = true; return; }
        CaptureMouse(); _dragStart = point; _dragBarsBack = _barsBack;
    }
    protected override void OnMouseRightButtonDown(MouseButtonEventArgs e)
    {
        var point = e.GetPosition(this); if (!TryPrice(point, out var price)) return;
        var tolerance = (_renderMax - _renderMin) / (decimal)Math.Max(20, ActualHeight / 8);
        var order = _orders.Where(o => o.IsWorking).OrderBy(o => Math.Abs((o.LimitPrice ?? o.StopPrice ?? decimal.MaxValue) - price)).FirstOrDefault();
        if (order is not null && Math.Abs((order.LimitPrice ?? order.StopPrice ?? decimal.MaxValue) - price) <= tolerance) { OrderCancelRequested?.Invoke(order); e.Handled = true; }
    }
    protected override void OnMouseLeftButtonUp(MouseButtonEventArgs e) { ReleaseMouseCapture(); _dragStart = null; }
    protected override void OnMouseMove(MouseEventArgs e)
    {
        var p = e.GetPosition(this); _crosshair = p;
        if (_dragStart.HasValue && e.LeftButton == MouseButtonState.Pressed)
        {
            var plotWidth = Math.Max(1, ActualWidth - 82);
            var barWidth = plotWidth / Math.Max(1, _visibleCount);
            _barsBack = Math.Clamp(_dragBarsBack + (int)((p.X - _dragStart.Value.X) / barWidth), 0, Math.Max(0, _candles.Count - _visibleCount));
        }
        InvalidateVisual();
    }
    protected override void OnMouseLeave(MouseEventArgs e) { if (!_dragStart.HasValue) _crosshair = null; InvalidateVisual(); }

    protected override void OnRender(DrawingContext dc)
    {
        dc.DrawRectangle(Frozen("#090D10"), null, new Rect(RenderSize));
        if (_candles.Count == 0 || ActualWidth < 150 || ActualHeight < 150) { Text(dc, "Waiting for price history", 16, 16, Brushes.Gray, 14); return; }
        var rightAxis = 78d; var top = 28d; var bottomAxis = 25d; var volumeHeight = Math.Max(65, ActualHeight * .19);
        var priceBottom = ActualHeight - bottomAxis - volumeHeight; var plotRight = ActualWidth - rightAxis;
        var count = Math.Min(_visibleCount, _candles.Count); var end = Math.Max(count, _candles.Count - _barsBack); var start = Math.Max(0, end - count);
        var data = _candles.Skip(start).Take(end - start).ToArray(); if (data.Length == 0) return;
        var min = data.Min(x => x.Low); var max = data.Max(x => x.High); var pad = Math.Max((max - min) * .06m, max * .001m); min -= pad; max += pad;
        var priceHeight = priceBottom - top; double Y(decimal v) => top + (double)((max - v) / Math.Max(.0000001m, max - min)) * priceHeight;
        _renderMin=min;_renderMax=max;_renderTop=top;_renderPriceBottom=priceBottom;_renderPlotRight=plotRight;
        var barW = plotRight / data.Length;
        for (var i = 0; i <= 5; i++)
        {
            var y = top + priceHeight * i / 5; dc.DrawLine(new Pen(Grid, .6), new Point(0, y), new Point(plotRight, y));
            var price = max - (max - min) * i / 5; Text(dc, price.ToString(price < 10 ? "F3" : "F2"), plotRight + 5, y - 8, Brushes.LightGray, 11);
        }
        DrawSessionLevels(dc, min, max, plotRight, Y);
        if (ShowLiquidity) DrawLiquidity(dc, min, max, plotRight, Y);
        DrawTradingLevels(dc, min, max, plotRight, Y);
        var maxVol = Math.Max(1L, data.Max(x => x.Volume));
        for (var i = 0; i < data.Length; i++)
        {
            var c = data[i]; var x = (i + .5) * barW; var brush = c.Close >= c.Open ? Up : Down;
            dc.DrawLine(new Pen(brush, 1), new Point(x, Y(c.High)), new Point(x, Y(c.Low)));
            var y1 = Y(Math.Max(c.Open, c.Close)); var y2 = Y(Math.Min(c.Open, c.Close));
            dc.DrawRectangle(brush, null, new Rect(x - Math.Max(1, barW * .32), y1, Math.Max(2, barW * .64), Math.Max(1, y2 - y1)));
            var vh = (double)c.Volume / maxVol * (volumeHeight - 18); dc.DrawRectangle(brush, null, new Rect(x - Math.Max(1, barW * .3), priceBottom + volumeHeight - vh, Math.Max(2, barW * .6), vh));
        }
        var legendX = 8d;
        if (ShowEma9) { DrawLine(dc, data, start, barW, Y, Ema(_candles, 9), Ema9Pen); Text(dc, "EMA 9", legendX, 6, Ema9Pen.Brush, 11); legendX += 54; }
        if (ShowEma20) { DrawLine(dc, data, start, barW, Y, Ema(_candles, 20), Ema20Pen); Text(dc, "EMA 20", legendX, 6, Ema20Pen.Brush, 11); legendX += 66; }
        if (ShowVwap) { DrawLine(dc, data, start, barW, Y, Vwap(_candles), VwapPen); Text(dc, "VWAP", legendX, 6, VwapPen.Brush, 11); }
        for (var i = 0; i < data.Length; i += Math.Max(1, data.Length / 7)) Text(dc, data[i].Timestamp.ToString(data.Length > 150 ? "MM/dd" : "MM/dd HH:mm"), i * barW, ActualHeight - 19, Brushes.Gray, 10);
        if (_crosshair is { } p && p.X >= 0 && p.X < plotRight && p.Y >= top && p.Y <= priceBottom)
        {
            var index = Math.Clamp((int)(p.X / barW), 0, data.Length - 1); var c = data[index]; var x = (index + .5) * barW;
            var dash = new Pen(Frozen("#78909C"), .8) { DashStyle = DashStyles.Dash }; dc.DrawLine(dash, new Point(x, top), new Point(x, ActualHeight - bottomAxis)); dc.DrawLine(dash, new Point(0, p.Y), new Point(plotRight, p.Y));
            var value = max - (decimal)((p.Y - top) / priceHeight) * (max - min); dc.DrawRectangle(Frozen("#37474F"), null, new Rect(plotRight, p.Y - 10, rightAxis, 20)); Text(dc, value.ToString(value < 10 ? "F3" : "F2"), plotRight + 5, p.Y - 8, Brushes.White, 11);
            dc.DrawRectangle(Frozen("#121A20"), null, new Rect(185, 2, 520, 22)); Text(dc, $"{c.Timestamp:g}   O {c.Open:F2}  H {c.High:F2}  L {c.Low:F2}  C {c.Close:F2}  V {c.Volume:N0}", 192, 6, Brushes.White, 11);
        }
    }

    private bool TryPrice(Point point, out decimal price)
    {
        price = 0; if (_renderMax <= _renderMin || point.X < 0 || point.X > _renderPlotRight || point.Y < _renderTop || point.Y > _renderPriceBottom) return false;
        price = _renderMax - (decimal)((point.Y-_renderTop)/Math.Max(1,_renderPriceBottom-_renderTop))*(_renderMax-_renderMin); return true;
    }

    private void DrawTradingLevels(DrawingContext dc, decimal min, decimal max, double right, Func<decimal,double> y)
    {
        if (_position is { IsFlat: false }) Level(dc, _position.AverageCost, $"POS {_position.Quantity:+#;-#} @ {_position.AverageCost:F2}", Frozen("#AB47BC"), min, max, right, y, DashStyles.Solid);
        if (_stagedPrice.HasValue) Level(dc, _stagedPrice.Value, $"STAGED {_stagedPrice:F2}", Frozen("#FFFFD54F"), min, max, right, y, DashStyles.Dash);
        foreach (var group in _orders.Where(o => o.IsWorking).GroupBy(o => (Price:o.LimitPrice ?? o.StopPrice, o.Side)).Where(g => g.Key.Price.HasValue))
        {
            var total=group.Sum(o=>o.QuantityRemaining); var count=group.Count(); var brush=group.Key.Side==OrderSide.Buy?Frozen("#00C853"):Frozen("#FF1744");
            Level(dc, group.Key.Price!.Value, $"{group.Key.Side.ToString().ToUpperInvariant()} {total:N0}"+(count>1?$" ×{count}":""), brush, min, max, right, y, DashStyles.Solid);
        }
    }

    private void DrawSessionLevels(DrawingContext dc, decimal min, decimal max, double right, Func<decimal, double> y)
    {
        var sessions = _candles.Where(c => c.Timestamp.DayOfWeek is not DayOfWeek.Saturday and not DayOfWeek.Sunday)
            .GroupBy(c => c.Timestamp.Date).OrderBy(g => g.Key).ToArray();
        if (sessions.Length == 0) return;
        var latestDate = sessions[^1].Key;
        var priorIndex = latestDate == DateTime.Today ? sessions.Length - 2 : sessions.Length - 1;
        if (priorIndex < 0) return;
        var prior = sessions[priorIndex].Where(c => c.Timestamp.TimeOfDay >= TimeSpan.FromHours(8.5) && c.Timestamp.TimeOfDay < TimeSpan.FromHours(15)).OrderBy(c => c.Timestamp).ToArray();
        if (prior.Length == 0) prior = sessions[priorIndex].OrderBy(c => c.Timestamp).ToArray();
        if (prior.Length == 0) return;
        var ph = prior.Max(c => c.High); var pl = prior.Min(c => c.Low); var po = prior[0].Open; var pc = prior[^1].Close;
        if (ShowPriorSession)
        {
            Level(dc, po, "Y OPEN", Frozen("#90A4AE"), min, max, right, y, DashStyles.Dash);
            Level(dc, ph, "Y HIGH", Frozen("#66BB6A"), min, max, right, y, DashStyles.Dash);
            Level(dc, pl, "Y LOW", Frozen("#EF5350"), min, max, right, y, DashStyles.Dash);
            Level(dc, pc, "Y CLOSE", Frozen("#FFCA28"), min, max, right, y, DashStyles.Dash);
        }
        if (ShowPremarket)
        {
            var pre = sessions[^1].Where(c => c.Timestamp.TimeOfDay >= TimeSpan.FromHours(3) && c.Timestamp.TimeOfDay < TimeSpan.FromHours(8.5)).ToArray();
            if (pre.Length > 0) { Level(dc, pre.Max(c => c.High), "PM HIGH", Frozen("#29B6F6"), min, max, right, y, DashStyles.Dot); Level(dc, pre.Min(c => c.Low), "PM LOW", Frozen("#AB47BC"), min, max, right, y, DashStyles.Dot); }
        }
        if (ShowCamarilla)
        {
            var range = ph - pl;
            var levels = new[] { (pc + range*1.1m/12,"R1"),(pc + range*1.1m/6,"R2"),(pc + range*1.1m/4,"R3"),(pc + range*1.1m/2,"R4"),(pc - range*1.1m/12,"S1"),(pc - range*1.1m/6,"S2"),(pc - range*1.1m/4,"S3"),(pc - range*1.1m/2,"S4") };
            foreach (var (price,label) in levels) Level(dc, price, $"CAM {label}", label[0]=='R' ? Frozen("#FF8A65") : Frozen("#4DB6AC"), min, max, right, y, DashStyles.Dot);
        }
    }

    private void DrawLiquidity(DrawingContext dc, decimal min, decimal max, double right, Func<decimal, double> y)
    {
        if (_depth is not { HasRealDepth: true }) return;
        var levels = _depth.Bids.Select(x => (x.Price, x.BidSize, true)).Concat(_depth.Asks.Select(x => (x.Price, x.AskSize, false)))
            .Where(x => x.Item1 >= min && x.Item1 <= max && x.Item2 > 0).OrderByDescending(x => x.Item2).Take(14).ToArray();
        var largest = levels.Select(x => x.Item2).DefaultIfEmpty(0).Max(); if (largest <= 0) return;
        foreach (var (price,size,bid) in levels)
        {
            var strength = Math.Sqrt((double)size / largest); var color = bid ? Color.FromArgb((byte)(70+150*strength), 0, 210, 150) : Color.FromArgb((byte)(70+150*strength), 255, 75, 65);
            var brush = new SolidColorBrush(color); brush.Freeze(); var yy = y(price); dc.DrawRectangle(brush, null, new Rect(0, yy-2-strength*4, right, 4+strength*8)); Text(dc, $"L2 {size:N0}", right-72, yy-7, Brushes.White, 10);
        }
    }

    private void Level(DrawingContext dc, decimal price, string label, Brush brush, decimal min, decimal max, double right, Func<decimal,double> y, DashStyle dash)
    {
        if (price < min || price > max) return; var yy = y(price); var pen = new Pen(brush, 1) { DashStyle = dash }; dc.DrawLine(pen, new Point(0,yy), new Point(right,yy)); Text(dc,label,4,yy-13,brush,10);
    }

    private static void DrawLine(DrawingContext dc, PriceCandle[] visible, int start, double barW, Func<decimal,double> y, decimal?[] values, Pen pen)
    {
        var geo = new StreamGeometry(); using var ctx = geo.Open(); var begun = false;
        for (var i = 0; i < visible.Length; i++) { var v = values[start + i]; if (!v.HasValue) continue; var p = new Point((i + .5) * barW, y(v.Value)); if (!begun) { ctx.BeginFigure(p, false, false); begun = true; } else ctx.LineTo(p, true, false); }
        geo.Freeze(); dc.DrawGeometry(null, pen, geo);
    }
    private static decimal?[] Ema(IReadOnlyList<PriceCandle> c, int period) { var r = new decimal?[c.Count]; if (c.Count == 0) return r; var k = 2m/(period+1); decimal e=c[0].Close; for(int i=0;i<c.Count;i++){e=i==0?c[i].Close:c[i].Close*k+e*(1-k);r[i]=e;} return r; }
    private static decimal?[] Vwap(IReadOnlyList<PriceCandle> c) { var r=new decimal?[c.Count]; decimal pv=0,v=0; DateTime? day=null; for(int i=0;i<c.Count;i++){if(day!=c[i].Timestamp.Date){pv=0;v=0;day=c[i].Timestamp.Date;} var vol=Math.Max(0,c[i].Volume);pv+=((c[i].High+c[i].Low+c[i].Close)/3)*vol;v+=vol;r[i]=v>0?pv/v:c[i].Close;}return r; }
    private void Text(DrawingContext dc,string s,double x,double y,Brush b,double size){dc.DrawText(new FormattedText(s,System.Globalization.CultureInfo.InvariantCulture,FlowDirection.LeftToRight,_font,size,b,VisualTreeHelper.GetDpi(this).PixelsPerDip),new Point(x,y));}
    private static SolidColorBrush Frozen(string hex){var b=(SolidColorBrush)new BrushConverter().ConvertFromString(hex)!;b.Freeze();return b;}
}
