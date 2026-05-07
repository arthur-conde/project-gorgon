using System.Globalization;
using System.Windows.Media;
using FluentAssertions;
using Legolas.Domain;
using Legolas.Views.Converters;

namespace Legolas.Tests.Settings;

public class PinShapeGeometryTests
{
    private const double Size = 10.0;

    [Theory]
    [InlineData(PinShape.Circle)]
    [InlineData(PinShape.Square)]
    [InlineData(PinShape.Diamond)]
    [InlineData(PinShape.Cross)]
    public void Each_shape_fills_its_size_box_within_tolerance(PinShape shape)
    {
        var geometry = PinShapeToGeometryConverter.BuildGeometry(shape, Size);
        var bounds = geometry.Bounds;

        // Allow a half-pixel of slack — RectangleGeometry's Bounds match
        // exactly, but PathGeometry/EllipseGeometry can compute slightly
        // different extents depending on tessellation.
        bounds.Left.Should().BeApproximately(0.0, 0.5);
        bounds.Top.Should().BeApproximately(0.0, 0.5);
        bounds.Right.Should().BeApproximately(Size, 0.5);
        bounds.Bottom.Should().BeApproximately(Size, 0.5);
    }

    [Fact]
    public void None_returns_empty_geometry()
    {
        var geometry = PinShapeToGeometryConverter.BuildGeometry(PinShape.None, Size);
        geometry.Should().BeSameAs(Geometry.Empty);
    }

    [Fact]
    public void Zero_or_negative_size_returns_empty_geometry()
    {
        var converter = PinShapeToGeometryConverter.Instance;
        var result = converter.Convert(new object[] { PinShape.Circle, 0.0 }, typeof(Geometry), null!, CultureInfo.InvariantCulture);
        result.Should().BeSameAs(Geometry.Empty);
    }

    [Fact]
    public void Multi_value_converter_wires_through_to_BuildGeometry()
    {
        var converter = PinShapeToGeometryConverter.Instance;
        var result = converter.Convert(new object[] { PinShape.Square, Size }, typeof(Geometry), null!, CultureInfo.InvariantCulture);
        result.Should().BeOfType<RectangleGeometry>();
        ((RectangleGeometry)result).Rect.Width.Should().Be(Size);
        ((RectangleGeometry)result).Rect.Height.Should().Be(Size);
    }
}
