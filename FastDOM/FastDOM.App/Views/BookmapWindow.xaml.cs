using System.Windows;
using FastDOM.App.ViewModels;

namespace FastDOM.App.Views;

public partial class BookmapWindow : Window
{
    private readonly DepthMapViewModel _viewModel;

    public BookmapWindow(DepthMapViewModel vm)
    {
        InitializeComponent();
        _viewModel = vm;
        DataContext = vm;
    }

    protected override void OnClosed(EventArgs e)
    {
        _viewModel.Dispose();
        base.OnClosed(e);
    }
}
