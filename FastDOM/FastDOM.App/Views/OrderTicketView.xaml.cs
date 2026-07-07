using System.Windows;
using System.Windows.Controls;

namespace FastDOM.App.Views;

public partial class OrderTicketView : UserControl
{
    public static readonly RoutedEvent QuickActionEvent =
        EventManager.RegisterRoutedEvent("QuickAction", RoutingStrategy.Bubble,
            typeof(QuickActionEventHandler), typeof(OrderTicketView));

    public event QuickActionEventHandler QuickAction
    {
        add => AddHandler(QuickActionEvent, value);
        remove => RemoveHandler(QuickActionEvent, value);
    }

    public OrderTicketView() => InitializeComponent();

    private void QuickAction_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button btn && btn.Tag is string action)
            RaiseEvent(new QuickActionEventArgs(QuickActionEvent, this, action));
    }
}

public delegate void QuickActionEventHandler(object sender, QuickActionEventArgs e);

public class QuickActionEventArgs : RoutedEventArgs
{
    public string Action { get; }
    public QuickActionEventArgs(RoutedEvent e, object source, string action) : base(e, source)
        => Action = action;
}
