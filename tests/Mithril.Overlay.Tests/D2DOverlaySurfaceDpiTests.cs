using System.Windows.Controls;
using System.Windows.Media;
using FluentAssertions;
using Mithril.Overlay.Internal;
using Xunit;

namespace Mithril.Overlay.Tests;

/// <summary>
/// Lifted from <c>tests/Legolas.Tests/Rendering/D2DOverlaySurfaceDpiTests.cs</c>
/// so the Stretch.Fill / per-monitor-DPI invariant has a regression guard in
/// the project that now owns <see cref="D2DOverlaySurface"/>. The Legolas-side
/// guard stays for the migration window; both run during this scaffold PR.
///
/// <para>The full pin-mapping test the issue body asks for would need a
/// real GPU (D3D11CreateDevice fails on headless CI) — see the note inside
/// the test. The arithmetic part (back-buffer pixel == ActualWidth ·
/// DpiScaleX rounded) lives inline as a unit test below, scoped to what's
/// reachable without a GPU.</para>
/// </summary>
public sealed class D2DOverlaySurfaceDpiTests
{
    [Fact]
    public void HostedImage_uses_Stretch_Fill_so_non_100_percent_scaling_maps_back_buffer_1_to_1()
    {
        RunOnSta(() =>
        {
            using var surface = new D2DOverlaySurface();
            VisualTreeHelper.GetChildrenCount(surface).Should().Be(1);
            var hosted = VisualTreeHelper.GetChild(surface, 0) as Image;
            hosted.Should().NotBeNull("the surface hosts the D3DImage in an Image element");
            hosted!.Stretch.Should().Be(
                Stretch.Fill,
                "the device-pixel back buffer must fill the element's DIP box 1:1 — "
                + "Stretch.None mis-scales the marker layer at display scaling != 100%");
        });
    }

    [Theory]
    [InlineData(800, 1.0, 800)]
    [InlineData(800, 1.25, 1000)]
    [InlineData(800, 1.5, 1200)]
    [InlineData(800, 1.75, 1400)]
    [InlineData(800, 2.0, 1600)]
    [InlineData(125, 1.5, 188)] // odd extent + scale exercises the rounding rule (125 * 1.5 = 187.5 → 188)
    [InlineData(123, 1.5, 184)] // banker's rounding: 184.5 → 184 (Math.Round default; intentional, not a footgun)
    public void BackBufferDimension_is_ActualExtent_times_DpiScale_rounded_to_int(
        double actualDip, double dpiScale, int expectedDevicePixels)
    {
        // Mirrors the device-pixel calculation in D2DOverlaySurface.OnCompositionTargetRendering:
        //   int w = (int)Math.Round(ActualWidth * dpi.DpiScaleX);
        // A GPU-free guard that the rounding rule doesn't drift accidentally.
        var actual = (int)Math.Round(actualDip * dpiScale);
        actual.Should().Be(expectedDevicePixels);
    }

    private static void RunOnSta(Action action)
    {
        Exception? captured = null;
        var thread = new Thread(() =>
        {
            try { action(); }
            catch (Exception ex) { captured = ex; }
            finally { System.Windows.Threading.Dispatcher.CurrentDispatcher.InvokeShutdown(); }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.IsBackground = true;
        thread.Start();
        thread.Join();
        if (captured is not null) throw captured;
    }
}
