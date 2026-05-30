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
        // Same world span (projected bbox 1000x800), but the texture is larger
        // than the bbox by a uniform border → the solved scale is smaller than
        // texture/span, and the inset fraction is the same on every edge: a
        // 100px margin on X (texW 1200 = 1000 + 100 each side) and an 80px
        // margin on Y (texH 960 = 800 + 80 each side).
        const double s = 2.0;
        const int texW = 1200, texH = 960;     // 1000x800 content + 100px X / 80px Y margin each side
        // WorldToWindow flips Y (pY = oy − s·Z): the 80px Y margin means world
        // Z=0 projects to pY = 880 (bottom inset 80) and Z=400 to pY = 80 (top
        // inset 80). originX 100 is the left inset; originY 880 is the Y-flip
        // anchor that places the bbox at the uniform 80px Y margin.
        var cal = new AreaCalibration(s, 0.0, 100.0, 880.0, 4, 0.0);
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
