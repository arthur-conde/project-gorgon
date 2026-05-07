using System.Globalization;
using System.Windows.Data;
using Legolas.Domain;

namespace Legolas.Views.Converters;

/// <summary>
/// Multiplexes <c>(PinStrokeStyle, configured thickness)</c> into the actual
/// rendered <c>StrokeThickness</c>: <see cref="PinStrokeStyle.None"/> forces 0
/// regardless of the configured thickness; Solid/Dashed pass through. Lets the
/// user choose a thickness once and toggle the stroke on/off via the style
/// dropdown without zeroing their value.
/// </summary>
public sealed class StrokeStyleToThicknessConverter : IMultiValueConverter
{
    public static readonly StrokeStyleToThicknessConverter Instance = new();

    public object Convert(object[] values, Type targetType, object? parameter, CultureInfo culture)
    {
        if (values.Length < 2) return 0.0;
        if (values[0] is not PinStrokeStyle style) return 0.0;
        if (style == PinStrokeStyle.None) return 0.0;
        return values[1] switch
        {
            double d => d,
            int i => (double)i,
            float f => (double)f,
            _ => 0.0,
        };
    }

    public object[] ConvertBack(object value, Type[] targetTypes, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}
