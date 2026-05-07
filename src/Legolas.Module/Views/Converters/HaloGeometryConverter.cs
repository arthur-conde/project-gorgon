using System.Globalization;
using System.Windows.Data;
using System.Windows.Media;
using Legolas.Domain;

namespace Legolas.Views.Converters;

/// <summary>
/// Builds the active-pin halo geometry. Takes <c>(outerShape, pinDiameter,
/// haloPaddingPx)</c> and produces a shape sized to <c>pinDiameter + 2 *
/// haloPaddingPx</c>, centred so the halo Path overlaps the outer pin shape
/// concentrically inside the Thumb's Grid.
/// </summary>
public sealed class HaloGeometryConverter : IMultiValueConverter
{
    public static readonly HaloGeometryConverter Instance = new();

    public object Convert(object[] values, Type targetType, object? parameter, CultureInfo culture)
    {
        if (values.Length < 3) return Geometry.Empty;
        if (values[0] is not PinShape shape) return Geometry.Empty;
        var diameter = ToDouble(values[1]);
        var padding = ToDouble(values[2]);
        var size = diameter + 2 * padding;
        return size <= 0 ? Geometry.Empty : PinShapeToGeometryConverter.BuildGeometry(shape, size);
    }

    public object[] ConvertBack(object value, Type[] targetTypes, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();

    private static double ToDouble(object? v) => v switch
    {
        double d => d,
        int i => i,
        float f => f,
        _ => 0.0,
    };
}
