using System.Windows;
using System.Windows.Controls;
using FastDOM.App.ViewModels;
using FastDOM.MarketData.Models;

namespace FastDOM.App.Views;

public partial class MoversWindow : Window
{
    private readonly MoversViewModel _viewModel;
    public event Action<string>? SymbolSelected;

    public MoversWindow(MoversViewModel viewModel)
    {
        InitializeComponent();
        DataContext = _viewModel = viewModel;
        Loaded += async (_, _) => await _viewModel.RefreshAsync();
    }

    private async void Refresh_Click(object sender, RoutedEventArgs e) => await _viewModel.RefreshAsync();

    private void Grid_MouseDoubleClick(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        if (sender is DataGrid { SelectedItem: MarketMover mover }) SymbolSelected?.Invoke(mover.Symbol);
    }
}
