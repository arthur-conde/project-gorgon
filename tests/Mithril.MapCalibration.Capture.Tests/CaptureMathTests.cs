using System;
using FluentAssertions;
using Mithril.MapCalibration.Capture;
using Xunit;

namespace Mithril.MapCalibration.Capture.Tests;

/// <summary>
/// #947: the capture region is a SHELL-persisted rect in PHYSICAL pixels, resolved
/// once at snip-confirm time from the snip window's single device scale (one-rect
/// model, spec §7). These tests pin the pure rect-math helpers the snip relies on:
/// <see cref="CaptureRectMath.DiuToPhysical"/> (which must match what
/// <c>BitBltScreenCapture</c> blits from <c>GetDC(NULL)</c> exactly — the decode/DPI
/// bug class from the #897 gate study) and <see cref="SnipRectMath"/> (drag
/// normalization + virtual-desktop offset). The live snip + drag are covered
/// separately (fail-soft test + manual-verify).
/// </summary>
public sealed class CaptureMathTests
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

    // ---- #947: snip-time DIU→physical resolution ----
    //
    // RegionSnipWindow.ToPhysical (private; needs a realized WPF window for
    // PresentationSource, so it's manual-verify) feeds the absolute-virtual-desktop
    // DIU selection + the snip window's single TransformToDevice scale into the pure
    // CaptureRectMath.DiuToPhysical helper. These tests pin that exact math for a
    // non-100% scale, so the persisted physical rect is provably correct: under
    // PerMonitorV2 one top-level window maps its whole logical surface uniformly at
    // one scale, so DIU·S_snip is the physical rect across the entire selection.

    [Fact]
    public void Snip_resolves_physical_rect_at_150_percent_scale()
    {
        // A DIU selection at (200, 100) sized 400x300, snipped on a window whose
        // single device scale is 1.5 → physical (300, 150, 600, 450).
        CaptureRectMath.DiuToPhysical(200, 100, 400, 300, 1.5, 1.5)
            .Should().Be(new CaptureRect(300, 150, 600, 450),
                "the snip persists DIU·S_snip uniformly across the whole selection");
    }

    [Fact]
    public void Snip_resolves_physical_rect_with_negative_virtual_origin()
    {
        // A selection straddling a negative virtual origin (secondary monitor left of
        // the primary) at 150% — the physical origin stays signed.
        CaptureRectMath.DiuToPhysical(-800, -100, 400, 200, 1.5, 1.5)
            .Should().Be(new CaptureRect(-1200, -150, 600, 300));
    }

    // ---- #957: physical→DIU for the overlay read path ----
    //
    // The overlay window reads its frame from the physical-px capture store and must
    // convert back to DIU to set Window.Left/Top/Width/Height. PhysicalToDiu is the
    // inverse of DiuToPhysical; round-tripping at the same scale must reproduce the
    // original integer rect (the binder positions the window where the snip framed it).

    [Fact]
    public void PhysicalToDiu_is_identity_at_100_percent()
    {
        CaptureRectMath.PhysicalToDiu(new CaptureRect(120, 80, 800, 600), 1.0, 1.0)
            .Should().Be((120.0, 80.0, 800.0, 600.0));
    }

    [Theory]
    [InlineData(1.50, 300, 150, 600, 450, 200, 100, 400, 300)]  // 150%
    [InlineData(2.00, 200, 100, 800, 600, 100, 50, 400, 300)]   // 200%
    public void PhysicalToDiu_divides_by_dpi(
        double scale,
        int px, int py, int pw, int ph,
        double dl, double dt, double dw, double dh)
    {
        CaptureRectMath.PhysicalToDiu(new CaptureRect(px, py, pw, ph), scale, scale)
            .Should().Be((dl, dt, dw, dh));
    }

    [Fact]
    public void PhysicalToDiu_preserves_negative_virtual_origin()
    {
        // Secondary monitor left/above the primary: the signed origin survives the
        // inverse scale just as it does the forward one.
        CaptureRectMath.PhysicalToDiu(new CaptureRect(-1200, -150, 600, 300), 1.5, 1.5)
            .Should().Be((-800.0, -100.0, 400.0, 200.0));
    }

    [Theory]
    [InlineData(1.0)]
    [InlineData(1.25)]
    [InlineData(1.5)]
    [InlineData(2.0)]
    public void PhysicalToDiu_round_trips_back_to_the_original_physical_rect(double scale)
    {
        var original = new CaptureRect(150, 90, 640, 480);
        var (l, t, w, h) = CaptureRectMath.PhysicalToDiu(original, scale, scale);
        // DIU → physical at the same scale must reproduce the integer rect the snip
        // framed (edge-rounding is a no-op on already-integer device edges).
        CaptureRectMath.DiuToPhysical(l, t, w, h, scale, scale).Should().Be(original);
    }

    [Theory]
    [InlineData(0, 0, 0, 0, 1.0, 1.0)]    // empty rect
    [InlineData(0, 0, 100, 100, 0.0, 1.0)] // bad scale
    public void PhysicalToDiu_returns_zero_on_degenerate_input(
        int px, int py, int pw, int ph, double sx, double sy)
    {
        CaptureRectMath.PhysicalToDiu(new CaptureRect(px, py, pw, ph), sx, sy)
            .Should().Be((0.0, 0.0, 0.0, 0.0));
    }
}
