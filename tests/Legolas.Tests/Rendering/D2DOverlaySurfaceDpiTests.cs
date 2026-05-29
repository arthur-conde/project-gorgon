using System;
using System.Threading;
using System.Windows.Controls;
using System.Windows.Media;
using FluentAssertions;
// #835: D2DOverlaySurface lifted to Mithril.Overlay.Internal. Test retained
// in tests/Legolas.Tests/ for the migration window; Mithril.Overlay.Tests now
// owns a parallel regression for the same Stretch.Fill invariant.
using Mithril.Overlay.Internal;
using Xunit;

namespace Legolas.Tests.Rendering;

/// <summary>
/// Regression guard for the non-100% display-scaling bug: the D2D back buffer
/// is allocated in device pixels and the hosting <c>D3DImage</c> stays at the
/// default 96 DPI, so the hosting <c>Image</c> must use <see cref="Stretch.Fill"/>.
/// With <see cref="Stretch.None"/> WPF maps one back-buffer pixel to one DIP,
/// mis-scaling the whole pin layer by the display-scale factor (pins drift off
/// the game map proportional to distance from the top-left; bottom-right pins
/// get clipped). <c>D2DOverlaySurface</c> is a <c>FrameworkElement</c> so the
/// test runs on a short-lived STA thread, matching <c>DetailExportHostSegmentTests</c>.
/// </summary>
public sealed class D2DOverlaySurfaceDpiTests
{
    [Fact]
    public void HostedImage_UsesStretchFill_SoNon100PercentScalingMapsBackBuffer1To1()
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
                + "Stretch.None mis-scales the pin layer at display scaling != 100%");
        });
    }

    private static void RunOnSta(Action action)
    {
        Exception? captured = null;
        var thread = new Thread(() =>
        {
            try
            {
                action();
            }
            catch (Exception ex)
            {
                captured = ex;
            }
            finally
            {
                System.Windows.Threading.Dispatcher.CurrentDispatcher.InvokeShutdown();
            }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.IsBackground = true;
        thread.Start();
        thread.Join();
        if (captured is not null) throw captured;
    }
}
