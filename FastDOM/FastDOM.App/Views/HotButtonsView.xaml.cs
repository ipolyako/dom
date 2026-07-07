using System.Windows;
using System.Windows.Controls;
using FastDOM.Core.Models;

namespace FastDOM.App.Views;

public class HotButtonExecutedEventArgs : RoutedEventArgs
{
    public HotButtonConfig Button { get; init; }
    public HotButtonExecutedEventArgs(RoutedEvent e, HotButtonConfig btn) : base(e) => Button = btn;
}

public partial class HotButtonsView : UserControl
{
    public static readonly RoutedEvent HotButtonExecutedEvent =
        EventManager.RegisterRoutedEvent("HotButtonExecuted", RoutingStrategy.Bubble,
            typeof(EventHandler<HotButtonExecutedEventArgs>), typeof(HotButtonsView));

    public static readonly RoutedEvent HotButtonSettingsRequestedEvent =
        EventManager.RegisterRoutedEvent("HotButtonSettingsRequested", RoutingStrategy.Bubble,
            typeof(RoutedEventHandler), typeof(HotButtonsView));

    public event EventHandler<HotButtonExecutedEventArgs> HotButtonExecuted
    {
        add => AddHandler(HotButtonExecutedEvent, value);
        remove => RemoveHandler(HotButtonExecutedEvent, value);
    }

    public event RoutedEventHandler HotButtonSettingsRequested
    {
        add => AddHandler(HotButtonSettingsRequestedEvent, value);
        remove => RemoveHandler(HotButtonSettingsRequestedEvent, value);
    }

    public HotButtonsView() => InitializeComponent();

    private void HotButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button btn && btn.Tag is HotButtonConfig cfg)
            RaiseEvent(new HotButtonExecutedEventArgs(HotButtonExecutedEvent, cfg) { Source = this });
    }

    private void HotButtonSettings_Click(object sender, RoutedEventArgs e)
        => RaiseEvent(new RoutedEventArgs(HotButtonSettingsRequestedEvent, this));
}
