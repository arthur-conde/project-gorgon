using FluentAssertions;
using Mithril.MapCalibration;
using Mithril.Tools.MapCalibrationStudy;
using Xunit;

namespace Mithril.Tools.MapCalibrationStudy.Tests;

public class OutlierGuardTests
{
    [Fact]
    public void Drops_a_single_axis_outlier_and_improves_residual()
    {
        const double s = 2.0, ox = 10, oy = 20;
        PixelPoint Px(double wx, double wz, double dx = 0, double dy = 0)
            => new(ox + s * wx + dx, oy - s * wz + dy);

        var refs = new List<LandmarkCalibrationSolver.Reference>
        {
            new(0, 0, Px(0, 0)),
            new(50, 0, Px(50, 0)),
            new(0, 40, Px(0, 40)),
            new(50, 40, Px(50, 40)),
            new(25, 20, Px(25, 20, dx: 30, dy: 0)), // +30px in X only — the outlier
        };

        var kept = OutlierGuard.Reject(refs, axisThresholdPx: 8.0);

        kept.Should().HaveCount(4);
        var cal = LandmarkCalibrationSolver.Solve(kept);
        cal!.ResidualPixels.Should().BeLessThan(1e-6);
    }

    [Fact]
    public void Keeps_all_when_no_axis_asymmetric_outlier()
    {
        const double s = 2.0, ox = 10, oy = 20;
        PixelPoint Px(double wx, double wz) => new(ox + s * wx, oy - s * wz);
        var refs = new List<LandmarkCalibrationSolver.Reference>
        {
            new(0, 0, Px(0, 0)), new(50, 0, Px(50, 0)),
            new(0, 40, Px(0, 40)), new(50, 40, Px(50, 40)),
        };
        OutlierGuard.Reject(refs, axisThresholdPx: 8.0).Should().HaveCount(4);
    }
}
