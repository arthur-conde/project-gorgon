using FluentAssertions;
using Legolas.Domain;
using Legolas.Services;
using Xunit;

namespace Legolas.Tests.Projection;

/// <summary>
/// #454: <see cref="AreaCalibration.ProjectWorld"/> must reproduce exactly the
/// transform <see cref="LandmarkCalibrationSolver"/> fits (it's the canonical
/// absolute world→pixel used by both ProcessMapFx placement and the landmark
/// "ghost" preview). Round-trips synthetic exact-fit references and pins the
/// <see cref="AreaCalibration.MirrorNorth"/> handedness.
/// </summary>
public class AreaCalibrationProjectWorldTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ProjectWorld_reproduces_the_solved_reference_pixels(bool mirrorNorth)
    {
        // A known similarity: pixel = origin + s·R(θ)·(east, north),
        // north = mirrorNorth ? -Z : Z — exactly the solver/Project model.
        const double s = 0.42;
        const double thetaDeg = 23.0;
        var theta = thetaDeg * Math.PI / 180.0;
        const double ox = 360.0, oy = 540.0;

        var world = new[]
        {
            new WorldCoord(-521.96, 0, 368.39),
            new WorldCoord(367.28, 0, 2798.03),
            new WorldCoord(1145.39, 0, 1323.40),
            new WorldCoord(1666.00, 0, -322.68),
        };

        PixelPoint Forward(WorldCoord w)
        {
            var east = w.X;
            var north = mirrorNorth ? -w.Z : w.Z;
            var rotE = east * Math.Cos(theta) + north * Math.Sin(theta);
            var rotN = -east * Math.Sin(theta) + north * Math.Cos(theta);
            return new PixelPoint(ox + s * rotE, oy - s * rotN);
        }

        var refs = world
            .Select(w => new LandmarkCalibrationSolver.Reference(w.X, w.Z, Forward(w)))
            .ToList();

        var cal = LandmarkCalibrationSolver.Solve(refs);
        cal.Should().NotBeNull();
        cal!.ResidualPixels.Should().BeLessThan(1e-6, "the references are an exact similarity");
        cal.MirrorNorth.Should().Be(mirrorNorth, "the lower-residual handedness must win");

        foreach (var w in world)
        {
            var projected = cal.ProjectWorld(w);
            var expected = Forward(w);
            projected.X.Should().BeApproximately(expected.X, 1e-6);
            projected.Y.Should().BeApproximately(expected.Y, 1e-6);
        }
    }

    [Fact]
    public void ProjectWorld_honours_MirrorNorth_flag()
    {
        // Same scale/rotation/origin, opposite handedness → Z sign flips the
        // north component, so the two projections differ for any Z != 0.
        var plus = new AreaCalibration(1.0, 0.0, 0, 0, 2, 0) { MirrorNorth = false };
        var minus = new AreaCalibration(1.0, 0.0, 0, 0, 2, 0) { MirrorNorth = true };
        var w = new WorldCoord(10, 0, 7);

        plus.ProjectWorld(w).Should().Be(new PixelPoint(10, -7));
        minus.ProjectWorld(w).Should().Be(new PixelPoint(10, 7));
    }
}
