using System;
using FluentAssertions;
using Mithril.MapCalibration.Capture;
using Xunit;

namespace Mithril.MapCalibration.Capture.Tests;

/// <summary>
/// #940: the capture region is the live overlay-window bounds converted to
/// physical desktop pixels (one-rect model, spec §7) — there is no longer a
/// separately-persisted rect (the old store + its round-trip are deleted). These
/// tests pin the pure rect-math helpers: <see cref="CaptureRectMath.DiuToPhysical"/>
/// (which must match what <c>BitBltScreenCapture</c> blits from <c>GetDC(NULL)</c>
/// exactly — the decode/DPI bug class from the #897 gate study) and
/// <see cref="SnipRectMath"/> (drag normalization + virtual-desktop offset). The
/// live overlay read + drag are covered separately (fail-soft test + manual-verify).
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

    // ---- #947: monitor selection + per-monitor DIU→physical affine map ----

    private static readonly MonitorDpiInfo Primary100 =
        new(DiuLeft: 0, DiuTop: 0, DiuWidth: 1920, DiuHeight: 1080,
            PhysicalLeft: 0, PhysicalTop: 0, ScaleX: 1.0, ScaleY: 1.0);

    [Fact]
    public void SelectMonitorScale_picks_the_monitor_containing_the_origin()
    {
        // Two 100% monitors side by side; the secondary sits left of the primary
        // with a NEGATIVE virtual origin.
        var secondaryLeft = new MonitorDpiInfo(-1920, 0, 1920, 1080, -1920, 0, 1.0, 1.0);
        var monitors = new[] { Primary100, secondaryLeft };

        MonitorScaleSelector.SelectMonitorScale(new MapCaptureRectDiu(-1800, 100, 200, 200), monitors)
            .Should().Be(secondaryLeft);
        MonitorScaleSelector.SelectMonitorScale(new MapCaptureRectDiu(50, 50, 200, 200), monitors)
            .Should().Be(Primary100);
    }

    [Fact]
    public void SelectMonitorScale_is_null_when_no_monitor_contains_the_origin()
    {
        MonitorScaleSelector.SelectMonitorScale(new MapCaptureRectDiu(99999, 99999, 10, 10), new[] { Primary100 })
            .Should().BeNull();
        MonitorScaleSelector.SelectMonitorScale(new MapCaptureRectDiu(0, 0, 10, 10), Array.Empty<MonitorDpiInfo>())
            .Should().BeNull();
    }

    [Fact]
    public void ToPhysical_is_identity_for_a_single_100_percent_monitor()
    {
        MonitorScaleSelector.ToPhysical(new MapCaptureRectDiu(120, 80, 800, 600), new[] { Primary100 })
            .Should().Be(new CaptureRect(120, 80, 800, 600));
    }

    [Fact]
    public void ToPhysical_uniform_dpi_multimonitor_negative_origin_is_exact()
    {
        // Uniform 150% layout: secondary monitor left of primary. Physical origin
        // and DIU origin scale consistently, so the affine map reduces to the proven
        // global scalar map (DIU·scale) — including the negative origin.
        // Secondary: physical [-2880,0]..[0,1620] (1920px ÷ ... actually 2880 phys =
        // 1920 DIU × 1.5), DIU [-1920,0].
        var primary = new MonitorDpiInfo(0, 0, 1280, 720, 0, 0, 1.5, 1.5);
        var secondary = new MonitorDpiInfo(-1920, 0, 1920, 1080, -2880, 0, 1.5, 1.5);
        var monitors = new[] { primary, secondary };

        // A rect on the secondary at DIU (-800,100), 400x300.
        // Global scalar expectation: DIU·1.5 → (-1200, 150, 600, 450).
        MonitorScaleSelector.ToPhysical(new MapCaptureRectDiu(-800, 100, 400, 300), monitors)
            .Should().Be(new CaptureRect(-1200, 150, 600, 450),
                "uniform-DPI affine map equals the global DIU·scale map");

        // A rect on the primary at DIU (100,50): DIU·1.5 → (150,75,600,450).
        MonitorScaleSelector.ToPhysical(new MapCaptureRectDiu(100, 50, 400, 300), monitors)
            .Should().Be(new CaptureRect(150, 75, 600, 450));
    }

    [Fact]
    public void ToPhysical_mixed_dpi_uses_per_monitor_scale_and_physical_origin()
    {
        // Primary 100% at physical/DIU [0,0]..[1920,1080].
        // Secondary 150% to the RIGHT: its physical origin is the primary's full
        // physical width (1920). In DIU it sits at 1920 (primary's DIU width). The
        // secondary's own DIU extent is its physical extent ÷ 1.5.
        var primary = new MonitorDpiInfo(0, 0, 1920, 1080, 0, 0, 1.0, 1.0);
        var secondary = new MonitorDpiInfo(
            DiuLeft: 1920, DiuTop: 0, DiuWidth: 1280, DiuHeight: 720,
            PhysicalLeft: 1920, PhysicalTop: 0, ScaleX: 1.5, ScaleY: 1.5);
        var monitors = new[] { primary, secondary };

        // A rect on the PRIMARY: identity.
        MonitorScaleSelector.ToPhysical(new MapCaptureRectDiu(100, 100, 200, 200), monitors)
            .Should().Be(new CaptureRect(100, 100, 200, 200));

        // A rect on the SECONDARY at DIU (1920+200, 100) = (2120,100), 100x100.
        // local DIU = (200,100); ·1.5 → (300,150) size (150,150); + physical origin
        // (1920,0) → (2220, 150, 150, 150). Note the SIZE uses the secondary's 1.5
        // scale (NOT the primary's), and the ORIGIN is re-anchored at the secondary's
        // physical origin — the per-monitor offset the global scalar map omits.
        MonitorScaleSelector.ToPhysical(new MapCaptureRectDiu(2120, 100, 100, 100), monitors)
            .Should().Be(new CaptureRect(2220, 150, 150, 150));
    }

    [Fact]
    public void ToPhysical_is_empty_when_no_monitor_contains_the_origin()
    {
        MonitorScaleSelector.ToPhysical(new MapCaptureRectDiu(99999, 99999, 100, 100), new[] { Primary100 })
            .IsEmpty.Should().BeTrue();
    }
}
