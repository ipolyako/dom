using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using FastDOM.App.ViewModels;

namespace FastDOM.App.Views;

public partial class BookmapWindow : Window
{
    private readonly DepthMapViewModel _defaultViewModel;
    private readonly List<DepthMapViewModel> _viewModels = [];

    public BookmapWindow(DepthMapViewModel vm, double defaultHeight, double defaultTop)
    {
        InitializeComponent();
        _defaultViewModel = vm;
        Height = Math.Max(MinHeight, defaultHeight);
        Top = defaultTop;
        AddLockedTab(vm);
    }

    private void AddLockedTab(DepthMapViewModel vm)
    {
        _viewModels.Add(vm);
        var header = new TextBlock
        {
            FontFamily = new System.Windows.Media.FontFamily("Consolas"),
            FontWeight = FontWeights.Bold,
            FontSize = 13,
            Foreground = (System.Windows.Media.Brush)FindResource("AccentBrush"),
            MinWidth = 80
        };
        header.SetBinding(TextBlock.TextProperty, new Binding(nameof(DepthMapViewModel.Symbol)) { Source = vm });
        SymbolTabs.Items.Add(new TabItem
        {
            Header = header,
            Content = new DepthMapView { DataContext = vm }
        });
        SymbolTabs.SelectedIndex = 0;
    }

    private void AddSymbolButton_Click(object sender, RoutedEventArgs e)
    {
        var vm = _defaultViewModel.CreateEditableTab();
        _viewModels.Add(vm);
        var symbolBox = new TextBox
        {
            Text = "Enter symbol",
            Width = 95,
            MaxLength = 24,
            CharacterCasing = CharacterCasing.Upper,
            FontFamily = new System.Windows.Media.FontFamily("Consolas"),
            FontWeight = FontWeights.Bold,
            FontSize = 13,
            Foreground = (System.Windows.Media.Brush)FindResource("AccentBrush"),
            VerticalContentAlignment = VerticalAlignment.Center,
            ToolTip = "Type a symbol and press Enter"
        };
        var close = new Button { Content = "×", Width = 22, Height = 22, Margin = new Thickness(5, 0, 0, 0) };
        var header = new StackPanel { Orientation = Orientation.Horizontal };
        header.Children.Add(symbolBox);
        header.Children.Add(close);
        var tab = new TabItem
        {
            Header = header,
            Content = new DepthMapView { DataContext = vm }
        };
        symbolBox.KeyDown += async (_, args) =>
        {
            if (args.Key != Key.Enter) return;
            args.Handled = true;
            await vm.SetSymbolAsync(symbolBox.Text);
            symbolBox.Text = vm.Symbol;
        };
        close.Click += (_, _) =>
        {
            vm.Dispose();
            _viewModels.Remove(vm);
            SymbolTabs.Items.Remove(tab);
        };
        SymbolTabs.Items.Add(tab);
        SymbolTabs.SelectedItem = tab;
        symbolBox.Focus();
        symbolBox.SelectAll();
    }

    protected override void OnClosed(EventArgs e)
    {
        foreach (var vm in _viewModels.ToArray()) vm.Dispose();
        _viewModels.Clear();
        base.OnClosed(e);
    }
}
