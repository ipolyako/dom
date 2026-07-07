using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using FastDOM.App.ViewModels;
using FastDOM.App.Services;
using FastDOM.Core.Enums;

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

    private void DomRow_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (sender is not FrameworkElement fe || fe.DataContext is not DomLadderRow row) return;
        if (ViewModel?.IsLocked == true) return;

        var modifiers = Keyboard.Modifiers;
        var colIndex = GetColumnIndex(e.GetPosition(fe), fe.ActualWidth);

        if (colIndex == 0)
        {
            // BUY column: cancel existing buy order at this price, or place new one
            if (ViewModel?.HasBuyOrderAt(row.Price) == true)
                _ = ViewModel.CancelOrdersAtPriceAsync(row.Price);
            else
                ViewModel?.OnBuyColumnClicked(row.Price, modifiers);
        }
        else if (colIndex == 4)
        {
            // SELL column: cancel existing sell order at this price, or place new one
            if (ViewModel?.HasSellOrderAt(row.Price) == true)
                _ = ViewModel.CancelOrdersAtPriceAsync(row.Price);
            else
                ViewModel?.OnSellColumnClicked(row.Price, modifiers);
        }
        else
        {
            ViewModel?.OnBuyColumnClicked(row.Price, modifiers);
        }

        RaiseEvent(new RoutedEventArgs(DomPriceLevelClickedEvent, this));
        e.Handled = true;
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
