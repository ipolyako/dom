using System.Windows.Controls;
using System.Windows.Input;
using FastDOM.App.ViewModels;

namespace FastDOM.App.Views;

public partial class WatchlistView : UserControl
{
    public WatchlistView() => InitializeComponent();

    private void NewSymbol_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter && DataContext is WatchlistViewModel vm)
            vm.AddSymbolCommand.Execute(null);
    }
}
