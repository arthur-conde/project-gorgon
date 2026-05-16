using System.Globalization;
using System.Windows;
using System.Windows.Data;

namespace Elrond.ViewModels;

/// <summary>Converts true to Visible, false to Collapsed. Pass ConverterParameter="invert" to flip.</summary>
public sealed class BoolToVisConverter : IValueConverter
{
    public static readonly BoolToVisConverter Instance = new();

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        var on = value is true;
        if (parameter is "invert") on = !on;
        return on ? Visibility.Visible : Visibility.Collapsed;
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}
