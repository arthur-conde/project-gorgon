using System.Globalization;
using System.Windows;
using System.Windows.Data;
using Samwise.State;

namespace Samwise.Views;

public sealed class StageToHarvestVisConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        => value is PlotStage s && s == PlotStage.Ripe ? Visibility.Visible : Visibility.Collapsed;
    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => throw new NotSupportedException();
}

public sealed class ZeroToVisConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        => value is int n && n == 0 ? Visibility.Visible : Visibility.Collapsed;
    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => throw new NotSupportedException();
}

public sealed class NonZeroToVisConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        => value is int n && n > 0 ? Visibility.Visible : Visibility.Collapsed;
    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => throw new NotSupportedException();
}

/// <summary>
/// Computes Canvas.Left = fraction × containerWidth − tickHalfWidth. Used to
/// position phase-transition ticks along a progress bar.
/// Bindings: [0] = Fraction (double 0..1), [1] = ActualWidth (double).
/// </summary>
public sealed class FractionToCanvasLeftConverter : IMultiValueConverter
{
    public object Convert(object[] values, Type targetType, object parameter, CultureInfo culture)
    {
        if (values.Length < 2) return 0.0;
        if (values[0] is not double fraction) return 0.0;
        if (values[1] is not double width) return 0.0;
        return (fraction * width) - 1.0; // center the 2px-wide tick
    }
    public object[] ConvertBack(object value, Type[] targetTypes, object parameter, CultureInfo culture) => throw new NotSupportedException();
}
