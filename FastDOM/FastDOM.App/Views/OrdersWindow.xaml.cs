using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Controls;
using FastDOM.App.Services;
using FastDOM.Core.Models;

namespace FastDOM.App.Views;

public partial class OrdersWindow : Window
{
    private readonly OrderService _orderService;
    private readonly string _accountId;

    public ObservableCollection<OrderState> Orders { get; } = [];

    public OrdersWindow(IEnumerable<OrderState> orders, OrderService orderService, string accountId)
    {
        InitializeComponent();
        DataContext = this;
        _orderService = orderService;
        _accountId = accountId;
        foreach (var o in orders)
            Orders.Add(o);
        UpdateStatus();
    }

    private void UpdateStatus()
    {
        StatusText.Text = Orders.Count == 0 ? "No open orders" : $"{Orders.Count} open order(s)";
    }

    private async void CancelRow_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button btn && btn.Tag is string orderId)
        {
            await _orderService.CancelOrderAsync(_accountId, orderId);
            var removed = Orders.FirstOrDefault(o => o.BrokerOrderId == orderId);
            if (removed != null) Orders.Remove(removed);
            UpdateStatus();
        }
    }

    private async void CancelAll_Click(object sender, RoutedEventArgs e)
    {
        var symbols = Orders.Select(o => o.Symbol).Distinct().ToList();
        foreach (var sym in symbols)
            await _orderService.CancelAllForSymbolAsync(_accountId, sym);
        Orders.Clear();
        UpdateStatus();
    }

    private void Close_Click(object sender, RoutedEventArgs e) => Close();
}
