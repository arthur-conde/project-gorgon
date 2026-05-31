using System;
using System.ComponentModel;
using System.Threading;
using System.Windows;
using FluentAssertions;
using Mithril.MapCalibration.Capture;
using Mithril.Overlay;
using Xunit;

namespace Mithril.MapCalibration.Capture.Tests;

/// <summary>
/// #940: <see cref="MapCaptureRegionProvider.Current"/> reads the LIVE overlay
/// bounds (one-rect model, spec §7). These tests run on an STA thread because WPF
/// <see cref="Window"/> construction requires it. They cover:
/// fail-soft <see langword="null"/> when the surface isn't shown (no
/// <see cref="System.Windows.PresentationSource"/>), region-derives-from-overlay-bounds
/// for a shown window, and the <c>ApplyVirtualDesktopRectToOverlay</c> set-bounds
/// seam. The DPI scaling itself is pinned by the pure
/// <see cref="CaptureRectMath"/> tests; here we assert the wiring at the live
/// (typically 100%) test-host DPI.
/// </summary>
public sealed class MapCaptureRegionProviderTests
{
    [Fact]
    public void Current_is_null_when_the_overlay_has_no_presentation_source() => RunOnSta(() =>
    {
        // A constructed-but-never-shown window has no HwndSource → fail-soft null.
        var window = new Window { Left = 100, Top = 100, Width = 400, Height = 300 };
        var provider = new MapCaptureRegionProvider(new RealWindowOverlay(window));

        provider.Current.Should().BeNull("an unshown overlay has no PresentationSource → fail soft");
    });

    [Fact]
    public void Current_derives_from_live_overlay_bounds_when_shown() => RunOnSta(() =>
    {
        var window = new Window
        {
            WindowStyle = WindowStyle.None,
            ShowInTaskbar = false,
            Left = 150,
            Top = 90,
            Width = 320,
            Height = 240,
        };
        try
        {
            window.Show();
            DrainDispatcher();

            var provider = new MapCaptureRegionProvider(new RealWindowOverlay(window));
            var rect = provider.Current;

            rect.Should().NotBeNull();
            // At the test host's DPI the conversion is the window's DIU rect scaled
            // by TransformToDevice; on a 100% host that is an identity, so the
            // origin/extent track the window. Assert via the same helper the
            // production path uses so the test is DPI-host-agnostic.
            var src = (System.Windows.Interop.HwndSource)System.Windows.PresentationSource.FromVisual(window)!;
            var m = src.CompositionTarget!.TransformToDevice;
            var expected = CaptureRectMath.DiuToPhysical(
                window.Left, window.Top, window.ActualWidth, window.ActualHeight, m.M11, m.M22);
            rect.Should().Be(expected);
        }
        finally { window.Close(); }
    });

    [Fact]
    public void Set_via_apply_seam_moves_and_resizes_only_bounds() => RunOnSta(() =>
    {
        var window = new Window
        {
            Topmost = true,
            WindowStyle = WindowStyle.None,
            AllowsTransparency = true,
            ShowInTaskbar = false,
            Left = 0,
            Top = 0,
            Width = 10,
            Height = 10,
        };

        MapBboxDrawController.ApplyVirtualDesktopRectToOverlay(window, new Rect(-200, 50, 640, 480));

        window.Left.Should().Be(-200);
        window.Top.Should().Be(50);
        window.Width.Should().Be(640);
        window.Height.Should().Be(480);

        // Forbidden invariants must be untouched.
        window.Topmost.Should().BeTrue();
        window.WindowStyle.Should().Be(WindowStyle.None);
        window.AllowsTransparency.Should().BeTrue();
    });

    [Fact]
    public void Apply_seam_ignores_degenerate_rect() => RunOnSta(() =>
    {
        var window = new Window { Left = 5, Top = 6, Width = 7, Height = 8 };
        MapBboxDrawController.ApplyVirtualDesktopRectToOverlay(window, new Rect(0, 0, 0, 100));
        window.Left.Should().Be(5);
        window.Width.Should().Be(7);
    });

    [Fact]
    public void BeginDraw_with_a_confirmed_snip_sets_overlay_bounds() => RunOnSta(() =>
    {
        var window = new Window
        {
            WindowStyle = WindowStyle.None,
            ShowInTaskbar = false,
            Left = 0,
            Top = 0,
            Width = 10,
            Height = 10,
        };
        try
        {
            window.Show();
            DrainDispatcher();

            var overlay = new RealWindowOverlay(window);
            var region = new Fixtures.FakeRegionProvider(null);
            // Inject a snip seam that returns a fixed confirmed rect (no live drag).
            var controller = new MapBboxDrawController(
                overlay, region, logger: null, snip: () => new Rect(120, 80, 500, 400));

            controller.BeginDraw();
            DrainDispatcher();

            window.Left.Should().Be(120);
            window.Top.Should().Be(80);
            window.Width.Should().Be(500);
            window.Height.Should().Be(400);
        }
        finally { window.Close(); }
    });

    [Fact]
    public void BeginDraw_with_a_cancelled_snip_leaves_bounds_unchanged() => RunOnSta(() =>
    {
        var window = new Window
        {
            WindowStyle = WindowStyle.None,
            ShowInTaskbar = false,
            Left = 33,
            Top = 44,
            Width = 55,
            Height = 66,
        };
        try
        {
            window.Show();
            DrainDispatcher();

            var controller = new MapBboxDrawController(
                new RealWindowOverlay(window), new Fixtures.FakeRegionProvider(null),
                logger: null, snip: () => null);

            controller.BeginDraw();
            DrainDispatcher();

            window.Left.Should().Be(33);
            window.Width.Should().Be(55);
        }
        finally { window.Close(); }
    });

    private static void DrainDispatcher() =>
        System.Windows.Threading.Dispatcher.CurrentDispatcher.Invoke(
            () => { }, System.Windows.Threading.DispatcherPriority.Loaded);

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

    /// <summary>A minimal real-window IOverlayWindow for the live-bounds tests.</summary>
    private sealed class RealWindowOverlay : IOverlayWindow
    {
        public RealWindowOverlay(Window window) => Window = window;
        public Window Window { get; }
        public bool IsReady => true;
        public string? StatusMessage { get; private set; }
        public void SetStatusMessage(string? message) { StatusMessage = message; PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(StatusMessage))); }
        public IDisposable RegisterScene(Action<IOverlaySceneContext> draw) => new Noop();
        public event PropertyChangedEventHandler? PropertyChanged;
        private sealed class Noop : IDisposable { public void Dispose() { } }
    }
}
