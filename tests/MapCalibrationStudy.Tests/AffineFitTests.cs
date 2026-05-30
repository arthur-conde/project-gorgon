using FluentAssertions;
using Mithril.Tools.MapCalibrationStudy;
using Xunit;

namespace Mithril.Tools.MapCalibrationStudy.Tests;

public class AffineFitTests
{
    private static (double wx, double wz, double px, double py) P(
        double wx, double wz, double s, double rot, double ox, double oy)
    {
        var cos = Math.Cos(rot); var sin = Math.Sin(rot);
        var rotE = wx * cos + wz * sin;
        var rotN = -wx * sin + wz * cos;
        return (wx, wz, ox + s * rotE, oy - s * rotN);
    }

    [Fact]
    public void Affine_fits_a_pure_similarity_with_near_zero_residual()
    {
        const double s = 1.7, rot = 0.0, ox = 12, oy = -5;
        var pts = new[]
        {
            P(0, 0, s, rot, ox, oy),
            P(100, 0, s, rot, ox, oy),
            P(0, 80, s, rot, ox, oy),
            P(100, 80, s, rot, ox, oy),
            P(40, 30, s, rot, ox, oy),
        };

        AffineFit.Rms(pts).Should().BeLessThan(1e-6);
    }

    [Fact]
    public void Affine_rms_never_exceeds_similarity_rms()
    {
        // Perturb one point so neither model is exact; affine (more DOF) must
        // still fit at least as tightly as the similarity.
        const double s = 1.7, rot = 0.0, ox = 12, oy = -5;
        var pts = new[]
        {
            P(0, 0, s, rot, ox, oy),
            P(100, 0, s, rot, ox, oy),
            P(0, 80, s, rot, ox, oy),
            (wx: 100.0, wz: 80.0, px: 999.0, py: -999.0), // outlier
            P(40, 30, s, rot, ox, oy),
        };

        AffineFit.Rms(pts).Should().BeLessThanOrEqualTo(AffineFit.SimilarityRms(pts) + 1e-9);
    }
}
