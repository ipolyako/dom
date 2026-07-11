using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using FastDOM.App.ViewModels;
using FastDOM.App.Services;
using FastDOM.Core.Enums;
using FastDOM.Core.Models;

namespace FastDOM.App.Views;

public partial class DomView : UserControl
{
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
    private DomViewModel? _wiredViewModel;
    private bool _centerQueued;

    public DomView()
    {
        InitializeComponent();
        DataContextChanged += (_, _) => WireViewModel();
    }

    private void WireViewModel()
    {
        if (_wiredViewModel != null)
        {
            _wiredViewModel.RowsUpdated -= OnRowsUpdated;
            _wiredViewModel.CenterRequested -= OnCenterRequested;
        }

        _wiredViewModel = ViewModel;
        if (_wiredViewModel == null) return;
        _wiredViewModel.Rows.CollectionChanged += (_, _) => QueueCenterOnLast(force: false);
        _wiredViewModel.RowsUpdated += OnRowsUpdated;
        _wiredViewModel.CenterRequested += OnCenterRequested;
    }

    private void OnRowsUpdated() => QueueCenterOnLast(force: false);
    private void OnCenterRequested() => QueueCenterOnLast(force: true);

    // Drag state for moving an existing working order to a new price.
    // Cancel is intentionally only wired to the × button on the order marker —
    // clicking anywhere else on an existing order starts (or is) a drag.
    private OrderState[]? _dragOrders;
    private Point _dragStartPos;
    private const double DragThresholdPx = 3.0;

    private void DomRow_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (sender is not FrameworkElement fe || fe.DataContext is not DomLadderRow row) return;
        if (IsDomRowCancelButtonClick(e.OriginalSource))
        {
            return;
        }
        if (ViewModel?.IsLocked == true) return;

        var modifiers = Keyboard.Modifiers;
        var colIndex = GetColumnIndex(e.GetPosition(fe), fe.ActualWidth);

        if (colIndex == 0 && row.BuyOrders.Count > 0)
        {
            BeginOrderDrag(fe, e, row.BuyOrders.Where(o => o.BrokerOrderId != null).ToArray(), row.Price, OrderSide.Buy);
            return;
        }
        if (colIndex == 4 && row.SellOrders.Count > 0)
        {
            BeginOrderDrag(fe, e, row.SellOrders.Where(o => o.BrokerOrderId != null).ToArray(), row.Price, OrderSide.Sell);
            return;
        }

        // No existing order in the clicked column — place a new one at the row price.
        if (colIndex == 0)
            ViewModel?.OnBuyColumnClicked(row.Price, modifiers);
        else if (colIndex == 4)
            ViewModel?.OnSellColumnClicked(row.Price, modifiers);
        else
            return;

        RaiseEvent(new RoutedEventArgs(DomPriceLevelClickedEvent, this));
        e.Handled = true;
    }

    private void BeginOrderDrag(FrameworkElement fe, MouseButtonEventArgs e, OrderState[] orders, decimal fromPrice, OrderSide side)
    {
        if (orders.Length == 0)
        {
            ViewModel?.ReportDragError($"drag: 0 {side} orders with BrokerOrderId at {fromPrice:F2}");
            return;
        }

        _dragOrders = orders
            .GroupBy(BuildDragOrderKey)
            .Select(g => g.First())
            .ToArray();
        _dragFromPrice = fromPrice;
        _dragSide = side;
        _dragStartPos = e.GetPosition(DomRows);
        fe.CaptureMouse();
        fe.Cursor = Cursors.SizeNS;
        e.Handled = true;

        // Prime the live preview at the source row.
        if (ViewModel != null)
        {
            ViewModel.DragTargetSide = side;
            ViewModel.DragTargetPrice = fromPrice;
            var total = _dragOrders.Sum(o => Math.Max(0, o.QuantityRemaining));
            ViewModel.DragPreviewSummary = _dragOrders.Length == 1
                ? total.ToString("N0")
                : $"{total:N0} ×{_dragOrders.Length}";
        }
    }

    private static string BuildDragOrderKey(OrderState order) =>
        $"{order.Symbol}|{order.Side}|{order.QuantityRemaining}|{order.LimitPrice:F4}|{order.StopPrice:F4}";

    private decimal _dragFromPrice;
    private OrderSide _dragSide;

    private void DomRow_MouseMove(object sender, MouseEventArgs e)
    {
        if (_dragOrders is null || _dragOrders.Length == 0) return;
        if (ViewModel == null) return;

        var pos = e.GetPosition(DomRows);
        var hover = FindRowAt(pos);
        if (hover == null) return;

        // Only update when the hovered row actually changes.
        if (ViewModel.DragTargetPrice != hover.Price)
            ViewModel.DragTargetPrice = hover.Price;
    }

    private void DomRow_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (sender is not FrameworkElement fe) return;
        if (IsDomRowCancelButtonClick(e.OriginalSource))
        {
            return;
        }

        fe.ReleaseMouseCapture();
        fe.Cursor = Cursors.Hand;

        if (_dragOrders is null || _dragOrders.Length == 0)
        {
            ClearDragPreview();
            return;
        }
        var orders = _dragOrders;
        var fromPrice = _dragFromPrice;
        _dragOrders = null;

        var endPos = e.GetPosition(DomRows);
        var deltaY = Math.Abs(endPos.Y - _dragStartPos.Y);
        if (deltaY < DragThresholdPx) { ClearDragPreview(); return; }

        var target = FindRowAt(endPos);
        if (target == null || target.Price == fromPrice) { ClearDragPreview(); return; }

        foreach (var o in orders)
            ViewModel?.OnOrderDragged(o, fromPrice, target.Price);

        ClearDragPreview();
        e.Handled = true;
    }

    private void ClearDragPreview()
    {
        if (ViewModel == null) return;
        ViewModel.DragTargetPrice = null;
        ViewModel.DragTargetSide = null;
        ViewModel.DragPreviewSummary = "";
    }

    private DomLadderRow? FindRowAt(Point pos)
    {
        var hit = VisualTreeHelper.HitTest(DomRows, pos)?.VisualHit as DependencyObject;
        while (hit != null)
        {
            if (hit is FrameworkElement fe && fe.DataContext is DomLadderRow row)
                return row;
            hit = VisualTreeHelper.GetParent(hit);
        }
        return null;
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

    private async void OrderMarkerCancel_Click(object sender, RoutedEventArgs e)
    {
        e.Handled = true;
        if (sender is not Button btn || btn.DataContext is not DomLadderRow row || ViewModel == null)
            return;

        var side = string.Equals(btn.Tag as string, "Buy", StringComparison.OrdinalIgnoreCase)
            ? OrderSide.Buy
            : OrderSide.Sell;

        var orders = (side == OrderSide.Buy ? row.BuyOrders : row.SellOrders)
            .Where(o => !string.IsNullOrWhiteSpace(o.BrokerOrderId))
            .DistinctBy(o => o.BrokerOrderId)
            .ToList();

        if (orders.Count == 0)
            return;

        if (orders.Count == 1)
        {
            await ViewModel.CancelOrderByIdAsync(orders[0].BrokerOrderId!);
            return;
        }

        ShowOrderPicker(btn, row.Price, orders);
    }

    private void ShowOrderPicker(Button anchor, decimal rowPrice, IReadOnlyList<OrderState> orders)
    {
        var menu = new ContextMenu();

        var title = new MenuItem
        {
            Header = $"{orders.Count} orders @ {rowPrice:F2}",
            IsEnabled = false,
            FontWeight = FontWeights.Bold
        };
        menu.Items.Add(title);
        menu.Items.Add(new Separator());

        foreach (var order in orders)
        {
            var item = new MenuItem { Header = "Cancel " + DescribeOrder(order, rowPrice) };
            item.Click += async (_, _) =>
            {
                if (ViewModel != null && !string.IsNullOrWhiteSpace(order.BrokerOrderId))
                    await ViewModel.CancelOrderByIdAsync(order.BrokerOrderId!);
            };
            menu.Items.Add(item);
        }

        menu.Items.Add(new Separator());
        var all = new MenuItem { Header = $"Cancel all {orders.Count} at this marker" };
        all.Click += async (_, _) =>
        {
            if (ViewModel == null) return;
            foreach (var order in orders)
            {
                if (!string.IsNullOrWhiteSpace(order.BrokerOrderId))
                    await ViewModel.CancelOrderByIdAsync(order.BrokerOrderId!);
            }
        };
        menu.Items.Add(all);

        anchor.ContextMenu = menu;
        menu.PlacementTarget = anchor;
        menu.IsOpen = true;
    }

    private static string DescribeOrder(OrderState order, decimal rowPrice)
    {
        var role = order.StopPrice.HasValue && order.StopPrice.Value == rowPrice
            ? "STP"
            : order.LimitPrice.HasValue && order.LimitPrice.Value == rowPrice
                ? "LMT"
                : order.OrderType.ToString();

        var limit = order.LimitPrice.HasValue ? $" L:{order.LimitPrice:F2}" : "";
        var stop = order.StopPrice.HasValue ? $" S:{order.StopPrice:F2}" : "";
        return $"{order.Side} {order.QuantityRemaining} {order.Symbol} {role}{limit}{stop}";
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
        if (ViewModel == null) return;
        ViewModel.IsLocked = !ViewModel.IsLocked;
        if (ViewModel.IsLocked)
            ViewModel.CenterLadderOnLast();
        QueueCenterOnLast(force: true);
    }

    private void CenterButton_Click(object sender, RoutedEventArgs e)
    {
        ViewModel?.CenterLadderOnLast();
        QueueCenterOnLast(force: true);
    }

    private void QueueCenterOnLast(bool force)
    {
        if (ViewModel == null) return;
        if (!force && !ViewModel.IsLocked) return;
        if (_centerQueued) return;

        _centerQueued = true;
        Dispatcher.InvokeAsync(() =>
        {
            _centerQueued = false;
            CenterOnLast();
        }, System.Windows.Threading.DispatcherPriority.Background);
    }

    private void CenterOnLast()
    {
        if (ViewModel == null) return;
        var lastIdx = ViewModel.Rows.ToList().FindIndex(r => r.IsLast);
        if (lastIdx >= 0)
        {
            var rowHeight = 18.0;
            var offset = Math.Max(0, (lastIdx * rowHeight) - (DomScroller.ViewportHeight / 2.0) + (rowHeight / 2.0));
            DomScroller.ScrollToVerticalOffset(offset);
        }
    }

    // Mirrors DomView.xaml row columns:
    // BUY(*), BID(60), PRICE(86), ASK(60), SELL(*), P/L(78).
    private static int GetColumnIndex(Point pt, double totalWidth)
    {
        var fixedWidth = 60d + 86d + 60d + 78d;
        var flexWidth = Math.Max(70d, (totalWidth - fixedWidth) / 2d);
        double[] widths = [flexWidth, 60d, 86d, 60d, flexWidth, 78d];
        double x = 0;
        for (int i = 0; i < widths.Length; i++)
        {
            x += widths[i];
            if (pt.X < x) return i;
        }
        return widths.Length - 1;
    }

    private static bool IsDomRowCancelButtonClick(object? source)
    {
        var current = source as DependencyObject;
        while (current != null)
        {
            if (current is Button button && button.Content is string content && content.Trim() == "×")
                return true;
            current = VisualTreeHelper.GetParent(current);
        }
        return false;
    }
}
