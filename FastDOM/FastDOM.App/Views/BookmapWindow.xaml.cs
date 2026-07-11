using System.Windows;
using FastDOM.App.ViewModels;

namespace FastDOM.App.Views;

public partial class BookmapWindow : Window
{
    public BookmapWindow(DepthMapViewModel vm)
    {
        InitializeComponent();
        DataContext = vm;
    }
}
