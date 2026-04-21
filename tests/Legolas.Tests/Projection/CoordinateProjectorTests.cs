using FluentAssertions;
using Legolas.Domain;
using Legolas.Services;

namespace Legolas.Tests.Projection;

public class CoordinateProjectorTests
{
    [Fact]
    public void Zero_rotation_projects_east_to_positive_x_and_north_to_negative_y()
    {
        var projector = new CoordinateProjector();
        projector.SetOrigin(new PixelPoint(100, 100));
        projector.CalibrateFromClick(
            playerPixel: new PixelPoint(100, 100),
            click: new PixelPoint(110, 100),
            offset: new MetreOffset(East: 5, North: 0));

        // scale should be 10/5 = 2 px/m, rotation ~ 0
        projector.Scale.Should().BeApproximately(2.0, 1e-6);
        projector.RotationRadians.Should().BeApproximately(0.0, 1e-6);

        var northProj = projector.Project(new MetreOffset(0, 5));
        northProj.X.Should().BeApproximately(100, 1e-6);
        northProj.Y.Should().BeApproximately(100 - 10, 1e-6); // north projects up (smaller y)
    }

    [Fact]
    public void Refit_recovers_known_scale_and_rotation()
    {
        // Ground truth: scale=3 px/m, rotation=30 deg (clockwise)
        const double expectedScale = 3.0;
        var expectedRotation = Math.PI / 6;

        var offsets = new[]
        {
            new MetreOffset(10, 0),
            new MetreOffset(0, 15),
            new MetreOffset(-8, 12),
            new MetreOffset(5, -7),
            new MetreOffset(20, 25),
        };

        var origin = new PixelPoint(500, 400);
        var truth = new CoordinateProjector();
        truth.SetOrigin(origin);
        // Seed scale and rotation via a synthetic calibration click at a known offset
        var syntheticCalibOffset = new MetreOffset(1, 0);
        truth.CalibrateFromClick(origin, new PixelPoint(500 + 3 * Math.Cos(expectedRotation),
                                                        400 + 3 * Math.Sin(expectedRotation)), syntheticCalibOffset);
        // That calibration sets rotation/scale based on the synthetic click; we verify later.

        // Build synthetic corrections from GROUND-TRUTH parameters directly.
        var corrections = new List<(MetreOffset, PixelPoint)>();
        foreach (var offset in offsets)
        {
            var cos = Math.Cos(expectedRotation);
            var sin = Math.Sin(expectedRotation);
            var rotE = offset.East * cos + offset.North * sin;
            var rotN = -offset.East * sin + offset.North * cos;
            var pixel = new PixelPoint(origin.X + expectedScale * rotE,
                                        origin.Y - expectedScale * rotN);
            corrections.Add((offset, pixel));
        }

        var fitted = new CoordinateProjector();
        fitted.SetOrigin(origin);
        fitted.Refit(corrections);

        fitted.Scale.Should().BeApproximately(expectedScale, 1e-6);
        fitted.RotationRadians.Should().BeApproximately(expectedRotation, 1e-6);
    }

    [Fact]
    public void Refit_is_noop_with_fewer_than_two_corrections()
    {
        var projector = new CoordinateProjector();
        projector.SetOrigin(new PixelPoint(0, 0));
        var originalScale = projector.Scale;

        projector.Refit(new[] { (new MetreOffset(5, 0), new PixelPoint(10, 0)) });

        projector.Scale.Should().Be(originalScale);
    }

    [Fact]
    public void Round_trip_projection_preserves_magnitude_with_unit_scale()
    {
        var projector = new CoordinateProjector();
        projector.SetOrigin(new PixelPoint(0, 0));
        projector.CalibrateFromClick(
            PixelPoint.Zero,
            new PixelPoint(0, -10),
            new MetreOffset(0, 10));

        projector.Scale.Should().BeApproximately(1.0, 1e-6);
        projector.RotationRadians.Should().BeApproximately(0.0, 1e-6);

        var projected = projector.Project(new MetreOffset(3, 4));
        projected.DistanceTo(PixelPoint.Zero).Should().BeApproximately(5.0, 1e-6);
    }
}
