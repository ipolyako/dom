using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using FastDOM.App.ViewModels;

namespace FastDOM.App.Views;

public partial class DepthMapView : UserControl
{
    private bool _isDragging;
    private double _lastDragY;
    private double _dragAccumulator;

    public DepthMapView()
    {
        InitializeComponent();
    }

    private DepthMapViewModel? ViewModel => DataContext as DepthMapViewModel;

    private void OnExpandClick(object sender, System.Windows.RoutedEventArgs e)
    {
        ViewModel?.Expand();
        e.Handled = true;
    }

    private void OnCompressClick(object sender, System.Windows.RoutedEventArgs e)
    {
        ViewModel?.Compress();
        e.Handled = true;
    }

    private void OnResetClick(object sender, System.Windows.RoutedEventArgs e)
    {
        ViewModel?.ResetScale();
        e.Handled = true;
    }

    private void OnSizeChanged(object sender, SizeChangedEventArgs e)
    {
        ViewModel?.SetViewportHeight(BookmapBody.ActualHeight);
    }

    private void OnMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (IsInsideButton(e.OriginalSource as DependencyObject)) return;

        _isDragging = true;
        _lastDragY = e.GetPosition(this).Y;
        _dragAccumulator = 0;
        CaptureMouse();
        e.Handled = true;
    }

    private void OnMouseMove(object sender, MouseEventArgs e)
    {
        if (!_isDragging || e.LeftButton != MouseButtonState.Pressed) return;

        var y = e.GetPosition(this).Y;
        _dragAccumulator += y - _lastDragY;
        _lastDragY = y;

        while (_dragAccumulator >= 16)
        {
            ViewModel?.Compress();
            _dragAccumulator -= 16;
        }

        while (_dragAccumulator <= -16)
        {
            ViewModel?.Expand();
            _dragAccumulator += 16;
        }

        e.Handled = true;
    }

    private void OnMouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (!_isDragging) return;

        _isDragging = false;
        _dragAccumulator = 0;
        ReleaseMouseCapture();
        e.Handled = true;
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
