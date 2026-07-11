using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using FastDOM.App.ViewModels;

namespace FastDOM.App.Views;

public partial class DepthMapView : UserControl
{
    private bool _dragging;
    private double _lastY;
    private double _accumulator;
    private const double DragStepPixels = 28;

    public DepthMapView() => InitializeComponent();

    private DepthMapViewModel? ViewModel => DataContext as DepthMapViewModel;

    private void OnZoomInClick(object sender, RoutedEventArgs e) => ViewModel?.ZoomIn();
    private void OnZoomOutClick(object sender, RoutedEventArgs e) => ViewModel?.ZoomOut();
    private void OnExpandLevelsClick(object sender, RoutedEventArgs e) => ViewModel?.ExpandLevels();
    private void OnContractLevelsClick(object sender, RoutedEventArgs e) => ViewModel?.ContractLevels();
    private void OnResetClick(object sender, RoutedEventArgs e) => ViewModel?.ResetScale();

    private void OnSizeChanged(object sender, SizeChangedEventArgs e) =>
        ViewModel?.SetViewportHeight(HeatBody.ActualHeight);

    private void OnMouseWheel(object sender, MouseWheelEventArgs e)
    {
        if (e.Delta > 0) ViewModel?.ZoomIn();
        else if (e.Delta < 0) ViewModel?.ZoomOut();
        e.Handled = true;
    }

    private void OnMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (IsInsideButton(e.OriginalSource as DependencyObject)) return;
        _dragging = true;
        _lastY = e.GetPosition(this).Y;
        _accumulator = 0;
        Cursor = Cursors.SizeNS;
        CaptureMouse();
        e.Handled = true;
    }

    private void OnMouseMove(object sender, MouseEventArgs e)
    {
        if (!_dragging || e.LeftButton != MouseButtonState.Pressed) return;
        var y = e.GetPosition(this).Y;
        _accumulator += y - _lastY;
        _lastY = y;
        while (_accumulator >= DragStepPixels)
        {
            ViewModel?.ZoomOut();
            _accumulator -= DragStepPixels;
        }
        while (_accumulator <= -DragStepPixels)
        {
            ViewModel?.ZoomIn();
            _accumulator += DragStepPixels;
        }
        e.Handled = true;
    }

    private void OnMouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (!_dragging) return;
        EndDrag();
        e.Handled = true;
    }

    private void OnLostMouseCapture(object sender, MouseEventArgs e) => EndDrag();

    private void EndDrag()
    {
        _dragging = false;
        _accumulator = 0;
        Cursor = Cursors.Arrow;
        if (IsMouseCaptured) ReleaseMouseCapture();
    }

    private static bool IsInsideButton(DependencyObject? source)
    {
        while (source != null)
        {
            if (source is Button) return true;
            source = VisualTreeHelper.GetParent(source);
        }
        return false;
    }
}
