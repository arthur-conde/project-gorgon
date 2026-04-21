using System.Globalization;
using System.Windows;
using System.Windows.Data;

namespace Legolas.Controls;

public sealed class BoolToVisibilityConverter : IValueConverter
{
    public static readonly BoolToVisibilityConverter Instance = new();

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        => value is true ? Visibility.Visible : Visibility.Collapsed;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}

public sealed class CorrectedToBrushConverter : IValueConverter
{
    public static readonly CorrectedToBrushConverter Instance = new();

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        => value is true
            ? System.Windows.Media.Brushes.Red    // finalized / user-corrected
            : System.Windows.Media.Brushes.Cyan;  // pending / projector-suggested (high contrast on outdoor map palettes)

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}
