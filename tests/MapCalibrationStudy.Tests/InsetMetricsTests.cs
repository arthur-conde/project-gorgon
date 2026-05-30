using FluentAssertions;
using Mithril.MapCalibration;
using Mithril.Tools.MapCalibrationStudy;
using Xunit;

namespace Mithril.Tools.MapCalibrationStudy.Tests;

public class InsetMetricsTests
{
    [Fact]
    public void No_inset_when_world_bbox_fills_texture()
    {
        const double s = 2.0;
        const int texW = 1000, texH = 800;
        // World bbox chosen so s*span == texture dim: spanX = 500, spanZ = 400.
        // Place 4 corner landmarks. WorldToWindow flips Y (north up: pY = oy − s·Z),
        // so for the bbox to fill the texture exactly the origin maps world(0,0,0)
        // → bottom-left pixel (0, texH) and world(500,0,400) → top-right (texW, 0).
        var cal = new AreaCalibration(s, 0.0, 0.0, texH, 4, 0.0);
        var world = new[]
        {
            new WorldCoord(0,   0, 0),
            new WorldCoord(500, 0, 0),
            new WorldCoord(0,   0, 400),
            new WorldCoord(500, 0, 400),
        };

        var m = InsetMetrics.Compute(cal, world, texW, texH);

        m.PredictedScaleX.Should().BeApproximately(s, 1e-9);
        m.PredictedScaleZ.Should().BeApproximately(s, 1e-9);
        m.ScaleRatioX.Should().BeApproximately(1.0, 1e-9);
        m.InsetFracMax.Should().BeApproximately(0.0, 1e-9);
    }

    [Fact]
    public void Detects_a_uniform_border_inset()
    {
        // Same world span, but the texture is 10% larger on each side than the
        // projected bbox → the solved scale is smaller than texture/span, and
        // the inset fraction is ~0.1 per edge in the larger dimension.
        const double s = 2.0;
        const int texW = 1200, texH = 960;     // 1000x800 content + 100px X / 80px Y margin each side
        // Y-flip again: a uniform Y margin of (960−800)/2 = 80px means world Z=0
        // projects to pY = 880 (bottom inset 80) and Z=400 to pY = 80 (top inset 80).
        var cal = new AreaCalibration(s, 0.0, 100.0, 880.0, 4, 0.0); // origin offset = the inset
        var world = new[]
        {
            new WorldCoord(0,   0, 0),
            new WorldCoord(500, 0, 0),
            new WorldCoord(0,   0, 400),
            new WorldCoord(500, 0, 400),
        };

        var m = InsetMetrics.Compute(cal, world, texW, texH);

        // predictedScale assumes no inset: texW/spanX = 1200/500 = 2.4 > s.
        m.PredictedScaleX.Should().BeApproximately(2.4, 1e-9);
        m.ScaleRatioX.Should().BeApproximately(s / 2.4, 1e-9);
        m.InsetFracMax.Should().BeApproximately(100.0 / 1200.0, 1e-6); // left margin / texW
    }
}
