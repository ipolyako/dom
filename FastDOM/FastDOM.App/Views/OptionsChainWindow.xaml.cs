using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using FastDOM.App.ViewModels;

namespace FastDOM.App.Views;

public partial class OptionsChainWindow : Window
{
    private readonly OptionsChainViewModel _vm;

    public OptionsChainWindow(OptionsChainViewModel vm)
    {
        InitializeComponent();
        _vm = vm;
        DataContext = vm;
    }

    private void Close_Click(object sender, RoutedEventArgs e) => Close();

    private void SymbolBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
            _vm.LoadExpirationsCommand.Execute(null);
    }

    private void ChainGrid_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        // Determine which column was clicked: 0-4 = calls, 5 = strike, 6-10 = puts
        var hit = e.OriginalSource as DependencyObject;
        var cell = FindAncestor<DataGridCell>(hit);
        if (cell == null) return;

        var row = FindAncestor<DataGridRow>(cell);
        if (row?.Item is not OptionsRowViewModel rowVm) return;

        var col = cell.Column;
        if (col == null) return;

        int colIdx = ChainGrid.Columns.IndexOf(col);

        if (colIdx >= 0 && colIdx <= 4)
            _vm.SelectCall(rowVm);
        else if (colIdx >= 6 && colIdx <= 10)
            _vm.SelectPut(rowVm);
        // column 5 (strike) — no action
    }

    private static T? FindAncestor<T>(DependencyObject? obj) where T : DependencyObject
    {
        while (obj != null)
        {
            if (obj is T t) return t;
            obj = System.Windows.Media.VisualTreeHelper.GetParent(obj);
        }
        return null;
    }
}
