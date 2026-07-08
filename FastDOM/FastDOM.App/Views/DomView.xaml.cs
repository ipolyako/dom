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

    // Drag state for moving an existing working order to a new price.
    // Cancel is intentionally only wired to the × button on the order marker —
    // clicking anywhere else on an existing order starts (or is) a drag.
    private OrderState[]? _dragOrders;
    private Point _dragStartPos;
    private const double DragThresholdPx = 3.0;

    private void DomRow_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (sender is not FrameworkElement fe || fe.DataContext is not DomLadderRow row) return;
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
            ViewModel?.OnBuyColumnClicked(row.Price, modifiers);

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

        _dragOrders = orders;
        _dragFromPrice = fromPrice;
        _dragStartPos = e.GetPosition(DomRows);
        fe.CaptureMouse();
        fe.Cursor = Cursors.SizeNS;
        e.Handled = true;
    }

    private decimal _dragFromPrice;

    private void DomRow_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (sender is not FrameworkElement fe) return;
        fe.ReleaseMouseCapture();
        fe.Cursor = Cursors.Hand;

        if (_dragOrders is null || _dragOrders.Length == 0) return;
        var orders = _dragOrders;
        var fromPrice = _dragFromPrice;
        _dragOrders = null;

        var endPos = e.GetPosition(DomRows);
        var deltaY = Math.Abs(endPos.Y - _dragStartPos.Y);
        if (deltaY < DragThresholdPx) return;

        var target = FindRowAt(endPos);
        if (target == null || target.Price == fromPrice) return;

        foreach (var o in orders)
            ViewModel?.OnOrderDragged(o, target.Price);

        e.Handled = true;
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
}
