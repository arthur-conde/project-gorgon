using System.Globalization;
using System.Windows.Data;

namespace Legolas.Controls;

/// <summary>
/// MultiBinding converter: returns coord - (size / 2). Used to place a square
/// element of arbitrary size centered on a given point in a Canvas.
/// </summary>
public sealed class CenterOffsetConverter : IMultiValueConverter
{
    public static readonly CenterOffsetConverter Instance = new();

    public object Convert(object[] values, Type targetType, object? parameter, CultureInfo culture)
    {
        if (values.Length != 2) return 0d;
        if (values[0] is not double coord) return 0d;
        if (values[1] is not double size) return coord;
        return coord - (size / 2.0);
    }

    public object[] ConvertBack(object value, Type[] targetTypes, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}
