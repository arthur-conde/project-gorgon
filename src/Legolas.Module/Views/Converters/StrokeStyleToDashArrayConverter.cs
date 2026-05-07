using System.Globalization;
using System.Windows.Data;
using System.Windows.Media;
using Legolas.Domain;

namespace Legolas.Views.Converters;

/// <summary>
/// <see cref="PinStrokeStyle.Dashed"/> → "4 2" dash pattern; everything else →
/// <c>null</c> (solid line, or no stroke when paired with thickness 0). The
/// "None" case still returns <c>null</c> here — visibility is enforced via
/// <see cref="StrokeStyleToThicknessConverter"/> which zeroes the thickness.
/// </summary>
public sealed class StrokeStyleToDashArrayConverter : IValueConverter
{
    public static readonly StrokeStyleToDashArrayConverter Instance = new();

    public object? Convert(object value, Type targetType, object? parameter, CultureInfo culture)
        => value is PinStrokeStyle.Dashed ? new DoubleCollection { 4, 2 } : null;

    public object ConvertBack(object value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}
