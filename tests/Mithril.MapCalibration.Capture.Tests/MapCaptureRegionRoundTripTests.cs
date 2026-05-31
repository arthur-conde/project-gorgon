using FluentAssertions;
using Mithril.MapCalibration.Capture;
using Xunit;

namespace Mithril.MapCalibration.Capture.Tests;

/// <summary>
/// #940: the capture region is the live overlay-window bounds converted to
/// physical desktop pixels (one-rect model, spec §7) — there is no longer a
/// separately-persisted rect. These tests pin the pure DIU→physical helper that
/// must match what <c>BitBltScreenCapture</c> blits from <c>GetDC(NULL)</c>
/// exactly (the decode/DPI bug class from the #897 gate study). The live overlay
/// read + drag are covered separately (fail-soft test + manual-verify).
/// </summary>
public sealed class MapCaptureRegionRoundTripTests
{
    [Fact]
    public void DiuToPhysical_is_identity_at_100_percent()
    {
        CaptureRectMath.DiuToPhysical(120, 80, 800, 600, 1.0, 1.0)
            .Should().Be(new CaptureRect(120, 80, 800, 600));
    }

    [Theory]
    [InlineData(1.25, 100, 50, 400, 300, 125, 63, 500, 375)]   // 125%
    [InlineData(1.50, 100, 50, 400, 300, 150, 75, 600, 450)]   // 150%
    [InlineData(2.00, 100, 50, 400, 300, 200, 100, 800, 600)]  // 200%
    public void DiuToPhysical_scales_by_dpi(
        double scale,
        double dl, double dt, double dw, double dh,
        int px, int py, int pw, int ph)
    {
        CaptureRectMath.DiuToPhysical(dl, dt, dw, dh, scale, scale)
            .Should().Be(new CaptureRect(px, py, pw, ph));
    }

    [Fact]
    public void DiuToPhysical_preserves_negative_virtual_origin()
    {
        // A secondary monitor left of the primary: negative DIU origin. BitBlt's
        // GetDC(NULL) addresses the same signed virtual-desktop frame, so the
        // physical origin must stay negative.
        CaptureRectMath.DiuToPhysical(-1920, -200, 600, 400, 1.0, 1.0)
            .Should().Be(new CaptureRect(-1920, -200, 600, 400));

        // ...and survive a scale: a 150% secondary screen at a negative origin.
        CaptureRectMath.DiuToPhysical(-800, -100, 400, 200, 1.5, 1.5)
            .Should().Be(new CaptureRect(-1200, -150, 600, 300));
    }

    [Fact]
    public void DiuToPhysical_rounds_edges_so_subpixel_does_not_shrink_extent()
    {
        // 133.33% (a common laptop scale): edge rounding keeps width faithful.
        var r = CaptureRectMath.DiuToPhysical(0, 0, 100, 100, 4.0 / 3.0, 4.0 / 3.0);
        r.Width.Should().Be(133);
        r.Height.Should().Be(133);
    }

    [Theory]
    [InlineData(0, 0, 0, 100, 1.0, 1.0)]    // zero width
    [InlineData(0, 0, 100, 0, 1.0, 1.0)]    // zero height
    [InlineData(0, 0, 100, 100, 0.0, 1.0)]  // bad scale
    [InlineData(double.NaN, 0, 100, 100, 1.0, 1.0)] // non-finite origin
    public void DiuToPhysical_returns_empty_on_degenerate_input(
        double dl, double dt, double dw, double dh, double sx, double sy)
    {
        CaptureRectMath.DiuToPhysical(dl, dt, dw, dh, sx, sy).IsEmpty.Should().BeTrue();
    }

    [Fact]
    public void SnipRectMath_normalizes_regardless_of_drag_direction()
    {
        var downRight = SnipRectMath.Normalize(new System.Windows.Point(10, 20), new System.Windows.Point(110, 220));
        var upLeft = SnipRectMath.Normalize(new System.Windows.Point(110, 220), new System.Windows.Point(10, 20));
        downRight.Should().Be(new System.Windows.Rect(10, 20, 100, 200));
        upLeft.Should().Be(downRight);
    }

    [Fact]
    public void SnipRectMath_offsets_local_rect_by_virtual_origin()
    {
        // Local canvas rect on a layout whose virtual origin is negative.
        var abs = SnipRectMath.ToVirtualDesktop(new System.Windows.Rect(50, 60, 300, 200), -1920, -100);
        abs.Should().Be(new System.Windows.Rect(-1870, -40, 300, 200));
    }
}
