using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using FastDOM.App.Services;
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
        Loaded += (_, _) => Width = Math.Min(Width, 560);
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

    private async void AddSymbolButton_Click(object sender, RoutedEventArgs e)
        => await AddEditableTabAsync(null);

    private async Task AddEditableTabAsync(string? initialSymbol)
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
        if (!string.IsNullOrWhiteSpace(initialSymbol))
        {
            await vm.SetSymbolAsync(initialSymbol);
            symbolBox.Text = vm.Symbol;
        }
        _ = Dispatcher.InvokeAsync(() =>
        {
            symbolBox.Focus();
            symbolBox.SelectAll();
        }, System.Windows.Threading.DispatcherPriority.Input);
    }

    public async Task RestoreLayoutAsync(L2WindowLayout layout)
    {
        foreach (var symbol in layout.Symbols.Where(s => !string.IsNullOrWhiteSpace(s)))
            await AddEditableTabAsync(symbol);
        SymbolTabs.SelectedIndex = Math.Clamp(layout.SelectedTab, 0, Math.Max(0, SymbolTabs.Items.Count - 1));
        if (layout.Maximized) WindowState = WindowState.Maximized;
    }

    public L2WindowLayout CaptureLayout()
    {
        var bounds = WindowState == WindowState.Maximized ? RestoreBounds : new Rect(Left, Top, Width, Height);
        return new L2WindowLayout
        {
            Left = bounds.Left, Top = bounds.Top, Width = bounds.Width, Height = bounds.Height,
            Maximized = WindowState == WindowState.Maximized,
            Symbols = _viewModels.Where(vm => vm.IsEditable && !string.IsNullOrWhiteSpace(vm.Symbol) && vm.Symbol != "New symbol")
                .Select(vm => vm.Symbol).ToList(),
            SelectedTab = SymbolTabs.SelectedIndex
        };
    }

    protected override void OnClosed(EventArgs e)
    {
        foreach (var vm in _viewModels.ToArray()) vm.Dispose();
        _viewModels.Clear();
        base.OnClosed(e);
    }
}
