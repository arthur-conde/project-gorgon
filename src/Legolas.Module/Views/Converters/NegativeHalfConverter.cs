using System.Globalization;
using System.Windows.Data;

namespace Legolas.Views.Converters;

/// <summary>
/// Maps a shape's <c>Size</c> to the <c>Canvas.Left</c>/<c>Canvas.Top</c>
/// offset (<c>-size/2</c>) that centres a
/// <see cref="PinShapeToGeometryConverter"/> geometry box (built as
/// <c>(0,0)–(size,size)</c>) on the marker's <c>(X,Y)</c>. Used by the in-flow
/// calibration marker template (#478): the marker sits in a zero-size Canvas
/// and each shape is offset by this so the dot stays pinned to the click
/// regardless of the selection-gated outer ring's size — a centring Grid would
/// re-flow and slide already-placed dots when selection moved (the documented
/// <c>wpf_grid_centers_fixed_child_marker_offset</c> pitfall).
/// </summary>
public sealed class NegativeHalfConverter : IValueConverter
{
    public static readonly NegativeHalfConverter Instance = new();

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        => -ToDouble(value) / 2.0;

    public object ConvertBack(object value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();

    private static double ToDouble(object? v) => v switch
    {
        double d => d,
        int i => i,
        float f => f,
        _ => 0.0,
    };
}
