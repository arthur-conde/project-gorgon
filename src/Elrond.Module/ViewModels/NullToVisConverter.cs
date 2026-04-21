using System.Globalization;
using System.Windows;
using System.Windows.Data;

namespace Elrond.ViewModels;

/// <summary>Converts non-null to Visible, null to Collapsed. Pass ConverterParameter="invert" to flip.</summary>
public sealed class NullToVisConverter : IValueConverter
{
    public static readonly NullToVisConverter Instance = new();

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        var isNull = value is null;
        if (parameter is "invert") isNull = !isNull;
        return isNull ? Visibility.Collapsed : Visibility.Visible;
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}
