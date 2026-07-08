using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using FastDOM.App.Services;
using FastDOM.Core.Enums;
using FastDOM.Core.Models;
using FastDOM.App.ViewModels;

namespace FastDOM.App.Views;

public partial class DomView : UserControl
{
    private const double DragThreshold = 3.0;

    public static readonly RoutedEvent DomPriceLevelClickedEvent =
        EventManager.RegisterRoutedEvent("DomPriceLevelClicked", RoutingStrategy.Bubble,
            typeof(RoutedEventHandler), typeof(DomView));

    public static readonly RoutedEvent DomContextMenuRequestedEvent =
        EventManager.RegisterRoutedEvent("DomContextMenuRequested", RoutingStrategy.Bubble,
            typeof(RoutedEventHandler), typeof(DomView));

    public event RoutedEventHandler DomPriceLevelClicked
    {
        add => AddHandler(DomPriceLevelClickedEvent, value);
        remove => RemoveHandler(DomPriceLevelClickedEvent, value);
    }

    public event RoutedEventHandler DomContextMenuRequested
    {
        add => AddHandler(DomContextMenuRequestedEvent, value);
        remove => RemoveHandler(DomContextMenuRequestedEvent, value);
    }

    private DomViewModel? ViewModel => DataContext as DomViewModel;

    private bool _mouseDown;
    private bool _isDragging;
    private bool _ignoreCloseButton;
    private Point _mouseDownPoint;
    private int _dragStartIndex;
    private DomLadderRow? _dragStartRow;
    private OrderSide? _dragSide;
    private List<OrderState> _dragOrders = [];

    public DomView()
    {
        InitializeComponent();
        DataContextChanged += (_, _) => WireViewModel();
    }

    private void WireViewModel()
    {
        if (ViewModel == null) return;
        ViewModel.Rows.CollectionChanged += (_, _) => CenterOnLast();
    }

    private void DomRow_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (sender is not FrameworkElement fe || fe.DataContext is not DomLadderRow row) return;
        if (ViewModel?.IsLocked == true) return;
        if (IsCloseButtonInteraction(e.OriginalSource))
        {
            _ignoreCloseButton = true;
            _mouseDown = false;
            _isDragging = false;
            _dragOrders = [];
            _dragSide = null;
            _dragStartRow = null;
            _dragStartIndex = -1;
            return;
        }

        _mouseDown = true;
        _isDragging = false;
        _ignoreCloseButton = false;
        _mouseDownPoint = e.GetPosition(DomRows);
        _dragStartRow = row;
        _dragStartIndex = ViewModel?.Rows.IndexOf(row) ?? -1;
        _dragOrders = [];
        _dragSide = GetOrderSideFromSource(e.OriginalSource);

        if (_dragSide == OrderSide.Buy)
        {
            _dragOrders = row.BuyOrders
                .Where(o => o.LimitPrice.HasValue && !string.IsNullOrWhiteSpace(o.BrokerOrderId))
                .ToList();
        }
        else if (_dragSide == OrderSide.Sell)
        {
            _dragOrders = row.SellOrders
                .Where(o => o.LimitPrice.HasValue && !string.IsNullOrWhiteSpace(o.BrokerOrderId))
                .ToList();
        }

        if (_dragSide == null || _dragOrders.Count == 0)
            return;

        fe.CaptureMouse();
        e.Handled = true;
    }

    private void DomRow_MouseMove(object sender, MouseEventArgs e)
    {
        if (!_mouseDown || _dragSide == null || ViewModel?.IsLocked == true)
            return;

        var point = e.GetPosition(DomRows);
        if (!_isDragging &&
            (Math.Abs(point.X - _mouseDownPoint.X) >= DragThreshold ||
             Math.Abs(point.Y - _mouseDownPoint.Y) >= DragThreshold))
        {
            _isDragging = true;
            if (_dragOrders.Count == 0)
                ViewModel?.ReportDragError("No draggable limit orders at this level. Drag a price level with limit order marker.");
        }
    }

    private void DomRow_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (sender is not FrameworkElement fe || fe.DataContext is not DomLadderRow row) return;
        if (_ignoreCloseButton || _dragStartRow == null)
        {
            ResetDragState(fe);
            return;
        }
        if (ViewModel?.IsLocked == true)
        {
            ResetDragState(fe);
            e.Handled = true;
            return;
        }

        if (_mouseDown && _dragSide != null)
        {
            var targetRow = HitTestRow(e.GetPosition(DomRows));
            targetRow ??= ResolveTargetRowByOffset(e.GetPosition(DomRows));
            var startRow = _dragStartRow;
            if (!_isDragging)
            {
                if (_dragSide == OrderSide.Buy)
                    _ = ViewModel.CancelOrdersAtPriceAsync(startRow.Price);
                else if (_dragSide == OrderSide.Sell)
                    _ = ViewModel.CancelOrdersAtPriceAsync(startRow.Price);
            }
            else if (_dragOrders.Count > 0 && targetRow is not null && startRow is not null && ViewModel is not null)
            {
                if (targetRow.Price != startRow.Price)
                {
                    foreach (var order in _dragOrders.ToList())
                        ViewModel.OnOrderDragged(order, targetRow.Price);
                }
                else
                {
                    ViewModel?.ReportDragError("Drag target is the same price level.");
                }
            }
            else
            {
                ViewModel?.ReportDragError("Drag target not found. Move to another visible DOM level before releasing.");
            }
        }
        else
        {
            ExecuteRowClick(row, fe, e.GetPosition(fe), Keyboard.Modifiers);
        }

        ResetDragState(fe);
        e.Handled = true;
    }

    private void ResetDragState(FrameworkElement source)
    {
        _ignoreCloseButton = false;
        _dragOrders = [];
        _dragSide = null;
        _dragStartRow = null;
        _dragStartIndex = -1;
        _mouseDown = false;
        _isDragging = false;
        source.ReleaseMouseCapture();
    }

    private static bool IsMarkerSource(object? source, string markerTag)
    {
        if (source is not DependencyObject node) return false;

        while (node != null)
        {
            if (node is FrameworkElement fe && Equals(fe.Tag, markerTag))
                return true;
            node = VisualTreeHelper.GetParent(node);
        }

        return false;
    }

    private static OrderSide? GetOrderSideFromSource(object? source)
    {
        if (IsMarkerSource(source, "BuyOrderMarker")) return OrderSide.Buy;
        if (IsMarkerSource(source, "SellOrderMarker")) return OrderSide.Sell;
        return null;
    }

    private void ExecuteRowClick(DomLadderRow row, FrameworkElement rowHost, Point rowPoint, ModifierKeys modifiers)
    {
        if (ViewModel == null) return;

        var colIndex = GetColumnIndex(rowPoint, rowHost.ActualWidth);
        if (colIndex == 0)
        {
            if (ViewModel.HasBuyOrderAt(row.Price))
                _ = ViewModel.CancelOrdersAtPriceAsync(row.Price);
            else
                ViewModel.OnBuyColumnClicked(row.Price, modifiers);
        }
        else if (colIndex == 4)
        {
            if (ViewModel.HasSellOrderAt(row.Price))
                _ = ViewModel.CancelOrdersAtPriceAsync(row.Price);
            else
                ViewModel.OnSellColumnClicked(row.Price, modifiers);
        }
        else
        {
            ViewModel.OnBuyColumnClicked(row.Price, modifiers);
        }

        RaiseEvent(new RoutedEventArgs(DomPriceLevelClickedEvent, this));
    }

    private DomLadderRow? HitTestRow(Point localPoint)
    {
        if (DomRows == null) return null;

        var result = VisualTreeHelper.HitTest(DomRows, localPoint);
        var node = result?.VisualHit as DependencyObject;
        while (node != null)
        {
            if (node is FrameworkElement fe && fe.DataContext is DomLadderRow row)
                return row;
            node = VisualTreeHelper.GetParent(node);
        }
        return null;
    }

    private DomLadderRow? ResolveTargetRowByOffset(Point localPoint)
    {
        if (ViewModel == null || _dragStartRow == null || _dragStartIndex < 0)
            return null;

        var rowHeight = DomRows
            .ItemContainerGenerator
            .ContainerFromItem(_dragStartRow) is FrameworkElement rowHost &&
            rowHost.ActualHeight > 0
            ? rowHost.ActualHeight
            : 20d;

        var yDelta = localPoint.Y - _mouseDownPoint.Y;
        var rowOffset = (int)Math.Round(yDelta / rowHeight, MidpointRounding.AwayFromZero);
        if (rowOffset == 0) return _dragStartRow;

        var targetIndex = Math.Clamp(_dragStartIndex + rowOffset, 0, Math.Max(0, ViewModel.Rows.Count - 1));
        return ViewModel.Rows[targetIndex];
    }

    private static bool IsCloseButtonInteraction(object? source)
    {
        if (source is not DependencyObject node) return false;

        while (node != null)
        {
            if (node is Button b && Equals(b.Tag, "DomCancelButton"))
                return true;
            node = VisualTreeHelper.GetParent(node);
        }

        return false;
    }

    private void DomRow_MouseRightButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (sender is not FrameworkElement fe || fe.DataContext is not DomLadderRow row) return;
        ViewModel?.OnRightClick(row.Price);
        ShowPriceContextMenu(row);
        e.Handled = true;
    }

    private void ShowPriceContextMenu(DomLadderRow row)
    {
        var menu = new ContextMenu();
        decimal price = row.Price;

        void Add(string header, Action action)
        {
            var item = new MenuItem { Header = header };
            item.Click += (_, _) => action();
            menu.Items.Add(item);
        }

        Add($"Buy Limit @ {price:F2}", () => ViewModel?.OnBuyColumnClicked(price, ModifierKeys.None));
        Add($"Sell Limit @ {price:F2}", () => ViewModel?.OnSellColumnClicked(price, ModifierKeys.None));
        menu.Items.Add(new Separator());
        Add($"Buy Stop @ {price:F2}", () => ViewModel?.OnBuyColumnClicked(price, ModifierKeys.Shift));
        Add($"Sell Stop @ {price:F2}", () => ViewModel?.OnSellColumnClicked(price, ModifierKeys.Shift));
        menu.Items.Add(new Separator());
        Add($"Buy Stop-Limit @ {price:F2}", () => ViewModel?.OnBuyColumnClicked(price, ModifierKeys.Control));
        Add($"Sell Stop-Limit @ {price:F2}", () => ViewModel?.OnSellColumnClicked(price, ModifierKeys.Control));
        menu.Items.Add(new Separator());
        Add($"Set Stop here ({price:F2})", () => SetStopHere(price));
        Add($"Set Target here ({price:F2})", () => SetTargetHere(price));
        menu.Items.Add(new Separator());
        Add("Cancel Orders at this Price", () => CancelOrdersAt(row));
        Add("Cancel All Buy Orders", () => CancelAllSide(OrderSide.Buy));
        Add("Cancel All Sell Orders", () => CancelAllSide(OrderSide.Sell));

        menu.IsOpen = true;
    }

    private void SetStopHere(decimal price)
    {
        if (ViewModel == null) return;
        // Populate order ticket with stop at this price
        if (ViewModel.Rows.FirstOrDefault() != null)
        {
            // Signal via VM
            ViewModel.ClickedPrice = price;
        }
    }

    private void SetTargetHere(decimal price) => SetStopHere(price);

    private void CancelOrdersAt(DomLadderRow row)
    {
        _ = ViewModel?.CancelOrdersAtPriceAsync(row.Price);
    }

    private void CancelAllSide(OrderSide side)
    {
        if (ViewModel?.CurrentAccountId == null) return;
        var toCancel = ViewModel.WorkingOrders.Where(o => o.Side == side).ToList();
        foreach (var o in toCancel)
            if (o.BrokerOrderId != null)
                _ = ViewModel.CancelOrdersAtPriceAsync(o.LimitPrice ?? 0);
    }

    private void LockButton_Click(object sender, RoutedEventArgs e)
    {
        if (ViewModel != null) ViewModel.IsLocked = !ViewModel.IsLocked;
    }

    private void CenterButton_Click(object sender, RoutedEventArgs e) => CenterOnLast();

    private void CenterOnLast()
    {
        if (ViewModel == null) return;
        var lastIdx = ViewModel.Rows.ToList().FindIndex(r => r.IsLast);
        if (lastIdx >= 0)
        {
            // Scroll to center on last traded price
            Dispatcher.Invoke(() =>
            {
                var container = DomRows.ItemContainerGenerator.ContainerFromIndex(lastIdx) as FrameworkElement;
                container?.BringIntoView();
            });
        }
    }

    // Divide the row width into 5 columns; return 0-4 index based on click x position
    private static int GetColumnIndex(Point pt, double totalWidth)
    {
        double[] widths = [70, 60, 0, 60, 70]; // last is flex
        double x = 0;
        for (int i = 0; i < 5; i++)
        {
            double colW = widths[i] > 0 ? widths[i] : totalWidth - 70 - 60 - 60 - 70;
            x += colW;
            if (pt.X < x) return i;
        }
        return 4;
    }

    private static int GetColumnIndexFromSource(object source, FrameworkElement rowHost, Point pointInRow)
    {
        if (source is DependencyObject node)
        {
            while (node != null)
            {
                if (node is FrameworkElement fe && fe != rowHost)
                {
                    var col = Grid.GetColumn(fe);
                    if (col >= 0 && col <= 4)
                        return col;
                }
                node = VisualTreeHelper.GetParent(node);
            }
        }

        return GetColumnIndex(pointInRow, rowHost.ActualWidth);
    }
}
