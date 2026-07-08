using System.Globalization;
using System.Windows;
using System.Windows.Data;
using System.Windows.Media;

namespace FastDOM.App.Converters;

/// <summary>
/// Row background: highlights bid (blue), ask (red), last (yellow tint), position (purple).
/// </summary>
[ValueConversion(typeof(bool), typeof(Brush))]
public class DomRowBrushConverter : IMultiValueConverter
{
    public object Convert(object[] values, Type targetType, object parameter, CultureInfo culture)
    {
        bool isBid = values.Length > 0 && values[0] is true;
        bool isAsk = values.Length > 1 && values[1] is true;
        bool isLast = values.Length > 2 && values[2] is true;
        bool isPos = values.Length > 3 && values[3] is true;

        if (isPos) return new SolidColorBrush(Color.FromArgb(60, 128, 0, 200));
        if (isLast) return new SolidColorBrush(Color.FromArgb(40, 255, 213, 79));
        if (isBid) return new SolidColorBrush(Color.FromArgb(30, 21, 101, 192));
        if (isAsk) return new SolidColorBrush(Color.FromArgb(30, 183, 28, 28));
        return Brushes.Transparent;
    }

    public object[] ConvertBack(object value, Type[] targetTypes, object parameter, CultureInfo culture)
        => throw new NotSupportedException();
}

[ValueConversion(typeof(int), typeof(Brush))]
public class OrderSummaryBrushConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        int count = value is int n ? n : 0;
        if (count == 0) return Brushes.Transparent;
        string side = parameter as string ?? "Buy";
        return side == "Buy"
            ? new SolidColorBrush(Color.FromRgb(27, 94, 32))
            : new SolidColorBrush(Color.FromRgb(127, 0, 0));
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => throw new NotSupportedException();
}

[ValueConversion(typeof(int), typeof(double))]
public class SizeToWidthConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        if (value is int size && size > 0)
            return Math.Min(58, size / 50.0);
        return 0.0;
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => throw new NotSupportedException();
}

[ValueConversion(typeof(bool), typeof(FontWeight))]
public class BoldConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        => value is true ? FontWeights.Bold : FontWeights.Normal;

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => throw new NotSupportedException();
}

[ValueConversion(typeof(bool), typeof(Visibility))]
public class BoolToVisibilityConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        => value is true ? Visibility.Visible : Visibility.Collapsed;

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => throw new NotSupportedException();
}

[ValueConversion(typeof(bool), typeof(Brush))]
public class LiveModeBrushConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        => value is true ? new SolidColorBrush(Color.FromRgb(180, 0, 0)) : new SolidColorBrush(Color.FromRgb(30, 100, 30));

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => throw new NotSupportedException();
}

[ValueConversion(typeof(bool), typeof(Brush))]
public class StaleBackgroundConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        => value is true ? new SolidColorBrush(Color.FromRgb(180, 40, 0)) : new SolidColorBrush(Color.FromRgb(30, 30, 30));

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => throw new NotSupportedException();
}

[ValueConversion(typeof(bool), typeof(Brush))]
public class StaleForeConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        => value is true ? Brushes.White : new SolidColorBrush(Color.FromRgb(120, 120, 120));

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => throw new NotSupportedException();
}

[ValueConversion(typeof(bool), typeof(Brush))]
public class ConnBrushConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        => value is true ? new SolidColorBrush(Color.FromRgb(27, 94, 32)) : new SolidColorBrush(Color.FromRgb(60, 60, 60));

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => throw new NotSupportedException();
}

// Shows the drag preview marker on exactly one row + one side.
// Bound with: [0]=row.Price, [1]=DomViewModel.DragTargetPrice, [2]=DomViewModel.DragTargetSide, param="Buy" or "Sell"
public class DragPreviewVisibilityConverter : IMultiValueConverter
{
    public object Convert(object[] values, Type targetType, object parameter, CultureInfo culture)
    {
        if (values.Length < 3) return Visibility.Collapsed;
        if (values[1] is not decimal target) return Visibility.Collapsed;
        if (values[0] is not decimal price) return Visibility.Collapsed;
        if (values[2] is not FastDOM.Core.Enums.OrderSide side) return Visibility.Collapsed;
        var sideParam = parameter as string;
        if (sideParam != side.ToString()) return Visibility.Collapsed;
        return price == target ? Visibility.Visible : Visibility.Collapsed;
    }

    public object[] ConvertBack(object value, Type[] targetTypes, object parameter, CultureInfo culture)
        => throw new NotSupportedException();
}

[ValueConversion(typeof(bool), typeof(Brush))]
public class OrdersBrushConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        => value is true ? new SolidColorBrush(Color.FromRgb(230, 81, 0)) : new SolidColorBrush(Color.FromRgb(42, 42, 42));

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => throw new NotSupportedException();
}

[ValueConversion(typeof(bool), typeof(Brush))]
public class HotkeyBrushConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        => value is true ? new SolidColorBrush(Color.FromRgb(21, 101, 192)) : new SolidColorBrush(Color.FromRgb(60, 60, 60));

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => throw new NotSupportedException();
}

[ValueConversion(typeof(bool), typeof(string))]
public class HotkeyLabelConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        => value is true ? "HK ARMED" : "HK OFF";

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => throw new NotSupportedException();
}

[ValueConversion(typeof(decimal), typeof(Brush))]
public class PnLBrushConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        if (value is decimal d) return d >= 0 ? new SolidColorBrush(Color.FromRgb(76, 175, 80)) : new SolidColorBrush(Color.FromRgb(244, 67, 54));
        return Brushes.Gray;
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => throw new NotSupportedException();
}

[ValueConversion(typeof(string), typeof(Brush))]
public class SideBrushConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        => (value as string) switch
        {
            "LONG"  => new SolidColorBrush(Color.FromRgb(76, 175, 80)),
            "SHORT" => new SolidColorBrush(Color.FromRgb(244, 67, 54)),
            _       => Brushes.Gray
        };

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => throw new NotSupportedException();
}
