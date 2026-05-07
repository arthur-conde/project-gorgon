using System.Globalization;
using System.Windows.Data;
using System.Windows.Media;
using Legolas.Domain;

namespace Legolas.Views.Converters;

/// <summary>
/// Maps <c>(PinShape, double size)</c> → <see cref="Geometry"/> for the
/// pin DataTemplate's outer + centre Paths. The geometry is built around
/// (0,0)–(size,size) so the Path occupies a known box; the Thumb container
/// centres the Grid containing both shapes.
/// </summary>
public sealed class PinShapeToGeometryConverter : IMultiValueConverter
{
    public static readonly PinShapeToGeometryConverter Instance = new();

    public object Convert(object[] values, Type targetType, object? parameter, CultureInfo culture)
    {
        if (values.Length < 2 || values[0] is not PinShape shape) return Geometry.Empty;
        var size = ToDouble(values[1]);
        if (size <= 0) return Geometry.Empty;
        return BuildGeometry(shape, size);
    }

    public object[] ConvertBack(object value, Type[] targetTypes, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();

    public static Geometry BuildGeometry(PinShape shape, double size) => shape switch
    {
        PinShape.Circle => new EllipseGeometry(new System.Windows.Point(size / 2, size / 2), size / 2, size / 2),
        PinShape.Square => new RectangleGeometry(new System.Windows.Rect(0, 0, size, size)),
        PinShape.Diamond => BuildDiamond(size),
        PinShape.Cross => BuildCross(size),
        PinShape.None => Geometry.Empty,
        _ => Geometry.Empty,
    };

    private static Geometry BuildDiamond(double size)
    {
        var half = size / 2;
        var fig = new PathFigure
        {
            StartPoint = new System.Windows.Point(half, 0),
            IsClosed = true,
            IsFilled = true,
        };
        fig.Segments.Add(new LineSegment(new System.Windows.Point(size, half), true));
        fig.Segments.Add(new LineSegment(new System.Windows.Point(half, size), true));
        fig.Segments.Add(new LineSegment(new System.Windows.Point(0, half), true));
        var path = new PathGeometry();
        path.Figures.Add(fig);
        return path;
    }

    private static Geometry BuildCross(double size)
    {
        // Cross = horizontal + vertical bar each ~1/3 the box thickness, centred.
        // Thinner than this and the cross disappears at small sizes; thicker
        // and it becomes a plus-sign block.
        var arm = size / 3;
        var offset = (size - arm) / 2;
        var group = new GeometryGroup();
        group.Children.Add(new RectangleGeometry(new System.Windows.Rect(0, offset, size, arm)));
        group.Children.Add(new RectangleGeometry(new System.Windows.Rect(offset, 0, arm, size)));
        return group;
    }

    private static double ToDouble(object? v) => v switch
    {
        double d => d,
        int i => i,
        float f => f,
        _ => 0.0,
    };
}
