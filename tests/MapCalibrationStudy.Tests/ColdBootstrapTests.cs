using FluentAssertions;
using Mithril.MapCalibration;
using Mithril.Tools.MapCalibrationStudy;
using Xunit;

namespace Mithril.Tools.MapCalibrationStudy.Tests;

public class ColdBootstrapTests
{
    // Known ground-truth transform for the synthetic area: scale, origin, and a
    // reflection (mirrorX/mirrorZ) — exercises orientation recovery.
    private static PixelPoint Project(WorldCoord w, double s, double ox, double oy, bool mirrorX, bool mirrorZ)
    {
        var x = mirrorX ? -w.X : w.X;
        var z = mirrorZ ? -w.Z : w.Z;
        return new PixelPoint(ox + s * x, oy - s * z);
    }

    [Fact]
    public void Recovers_orientation_correspondence_and_transform_cold()
    {
        // ColdBootstrap's rough prediction assumes the world bbox fills the
        // texture (scale0 = texture/span), so the synthetic detected cloud must
        // also roughly fill the texture for nearest-neighbour pairing to match —
        // that low-inset regime IS the H3 hypothesis the engine relies on. The
        // origin centres the projected bbox in the texture; the texture is sized
        // to the projection so scale0 ≈ the true scale.
        const double s = 1.5;
        const bool mirrorX = true, mirrorZ = false; // a non-trivial orientation
        const int texW = 512, texH = 384;
        const double ox = 485, oy = 351; // centres the mirrored projected bbox in the texture
        // ASYMMETRIC landmark cloud: the set is NOT point-symmetric about its
        // centroid, so the wrong (reflected) orientation cannot mispair all
        // landmarks to clean reflection-partner icons — only the true
        // orientation reprojects the whole detected set consistently. (A
        // point-symmetric cloud is genuinely 0°/180° ambiguous; real PG areas
        // aren't, and neither is this fixture.) The two interior points break
        // the corners' symmetry; the corners' own centre (150,110) differs from
        // the full-set centroid, so no landmark maps onto another under 180°.
        var world = new List<WorldCoord>
        {
            new(0, 0, 0), new(300, 0, 0), new(0, 0, 220),
            new(300, 0, 220), new(225, 0, 55), new(90, 0, 140),
        };
        // Detected icons = ground-truth projections, shuffled (detection has no
        // landmark identity — pairing must be inferred by geometry).
        var detected = world.Select(w => Project(w, s, ox, oy, mirrorX, mirrorZ)).ToList();
        var shuffled = new List<PixelPoint> { detected[3], detected[0], detected[5], detected[1], detected[4], detected[2] };

        var result = ColdBootstrap.Run(world, shuffled, textureW: texW, textureH: texH, axisThresholdPx: 8.0);

        result.Should().NotBeNull();
        result!.CorrespondedCount.Should().Be(6);
        // The winning orientation reprojects the WHOLE detected set consistently
        // (global score near zero) — this is the metric that separates the true
        // orientation from a reflected one that merely fit a subset.
        result.GlobalReprojectionPx.Should().BeLessThan(1.0);

        // The decisive H4 assertion: pin the RECOVERED TRANSFORM, not just the
        // count + a subset residual. A wrong-orientation run can also hit
        // count==6 and a near-zero subset residual (it fit a DIFFERENT subset),
        // so those alone don't prove blind recovery. Assert (parameterization-
        // agnostic) that the recovered calibration reprojects every world
        // landmark back onto its TRUE detected pixel — which only the
        // ground-truth orientation can do.
        foreach (var w in world)
        {
            var expected = Project(w, s, ox, oy, mirrorX, mirrorZ);
            var actual = result.Calibration.WorldToWindow(w);
            actual.X.Should().BeApproximately(expected.X, 1.0);
            actual.Y.Should().BeApproximately(expected.Y, 1.0);
        }
    }

    [Fact]
    public void Survives_a_one_axis_outlier_detection()
    {
        const double s = 1.5, ox = 600, oy = 700;
        var world = new List<WorldCoord>
        {
            new(0, 0, 0), new(200, 0, 0), new(0, 0, 150),
            new(200, 0, 150), new(90, 0, 60), new(40, 0, 120),
        };
        var detected = world.Select(w => Project(w, s, ox, oy, mirrorX: false, mirrorZ: false)).ToList();
        detected[4] = new PixelPoint(detected[4].X + 40, detected[4].Y); // +40px X only

        var result = ColdBootstrap.Run(world, detected, textureW: 1280, textureH: 1024, axisThresholdPx: 8.0);

        result.Should().NotBeNull();
        result!.RefinedResidualPx.Should().BeLessThan(1.0); // outlier dropped
    }
}
