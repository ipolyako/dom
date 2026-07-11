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

    public BookmapWindow(DepthMapViewModel vm, double defaultHeight, double defaultTop, double defaultLeft)
    {
        InitializeComponent();
        _defaultViewModel = vm;
        WindowStartupLocation = WindowStartupLocation.Manual;
        Height = Math.Max(MinHeight, defaultHeight);
        Top = defaultTop;
        Left = defaultLeft;
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
        var tab = new TabItem
        {
            Header = header,
            Content = new DepthMapView { DataContext = vm }
        };
        header.PreviewMouseLeftButtonDown += (_, _) => SymbolTabs.SelectedItem = tab;
        SymbolTabs.Items.Add(tab);
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
            var text = symbolBox.Text?.Trim().ToUpperInvariant();
            if (string.IsNullOrWhiteSpace(text)) return;
            args.Handled = true;
            await vm.SetSymbolAsync(text);
            symbolBox.Text = vm.Symbol;
            _ = Dispatcher.InvokeAsync(() =>
            {
                symbolBox.Focus();
                symbolBox.SelectAll();
            }, System.Windows.Threading.DispatcherPriority.Input);
        };
        symbolBox.GotKeyboardFocus += (_, _) =>
        {
            SymbolTabs.SelectedItem = tab;
            symbolBox.SelectAll();
        };
        symbolBox.PreviewMouseLeftButtonDown += (_, args) =>
        {
            SymbolTabs.SelectedItem = tab;
            if (symbolBox.IsKeyboardFocusWithin) return;
            args.Handled = true;
            symbolBox.Focus();
            symbolBox.SelectAll();
        };
        close.Click += (_, _) =>
        {
            vm.Dispose();
            _viewModels.Remove(vm);
            SymbolTabs.Items.Remove(tab);
        };
        SymbolTabs.Items.Add(tab);
        SymbolTabs.SelectedItem = tab;
        _ = Dispatcher.InvokeAsync(() =>
        {
            symbolBox.Focus();
            symbolBox.SelectAll();
        }, System.Windows.Threading.DispatcherPriority.Input);
    }

    protected override void OnClosed(EventArgs e)
    {
        foreach (var vm in _viewModels.ToArray()) vm.Dispose();
        _viewModels.Clear();
        base.OnClosed(e);
    }
}
