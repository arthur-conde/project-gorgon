using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Threading;
using System.Windows;
using FluentAssertions;
using Mithril.MapCalibration.Capture;
using Mithril.Overlay;
using Xunit;

namespace Mithril.MapCalibration.Capture.Tests;

/// <summary>
/// #947: <see cref="MapCaptureRegionProvider.Current"/> reads the SHELL-persisted
/// capture rect (not the live overlay-window geometry) and converts it to physical
/// pixels via the per-monitor DPI layout. These tests cover the fail-soft cases
/// (null store dependency, null persisted value, off-screen rect → <c>Current</c>
/// null) and the happy path (persisted DIU rect → converted physical rect, with NO
/// overlay window involved). They use a fake store + a fake monitor provider, so no
/// WPF window / STA thread is needed for the provider itself.
///
/// <para>The bbox draw controller tests (snip → store + overlay-bounds apply seam)
/// still run on an STA thread because WPF <see cref="Window"/> construction requires
/// it.</para>
/// </summary>
public sealed class MapCaptureRegionProviderTests
{
    private static readonly MonitorDpiInfo Primary100 =
        new(DiuLeft: 0, DiuTop: 0, DiuWidth: 1920, DiuHeight: 1080,
            PhysicalLeft: 0, PhysicalTop: 0, ScaleX: 1.0, ScaleY: 1.0);

    [Fact]
    public void Current_is_null_when_store_dependency_is_absent()
    {
        // A unit-test graph without the shell: no store wired → fail soft.
        var provider = new MapCaptureRegionProvider(store: null, monitors: new FakeMonitors(Primary100));
        provider.Current.Should().BeNull();
    }

    [Fact]
    public void Current_is_null_when_no_rect_has_been_snipped()
    {
        var provider = new MapCaptureRegionProvider(new FakeStore(value: null), new FakeMonitors(Primary100));
        provider.Current.Should().BeNull("never-snipped store value is the legitimate 'no bbox set' state");
    }

    [Fact]
    public void Current_converts_persisted_diu_rect_to_physical_with_no_overlay_window()
    {
        var store = new FakeStore(new MapCaptureRectDiu(120, 80, 800, 600));
        var provider = new MapCaptureRegionProvider(store, new FakeMonitors(Primary100));

        provider.Current.Should().Be(new CaptureRect(120, 80, 800, 600),
            "at 100% on the primary monitor the DIU→physical map is the identity");
    }

    [Fact]
    public void Current_scales_persisted_rect_by_monitor_dpi()
    {
        var monitor = new MonitorDpiInfo(0, 0, 1280, 720, 0, 0, 1.5, 1.5);
        var store = new FakeStore(new MapCaptureRectDiu(100, 50, 400, 300));
        var provider = new MapCaptureRegionProvider(store, new FakeMonitors(monitor));

        provider.Current.Should().Be(new CaptureRect(150, 75, 600, 450));
    }

    [Fact]
    public void Current_is_null_when_persisted_rect_is_off_every_monitor()
    {
        // A stale rect on a monitor that no longer exists → no containing monitor → null.
        var store = new FakeStore(new MapCaptureRectDiu(-5000, -5000, 100, 100));
        var provider = new MapCaptureRegionProvider(store, new FakeMonitors(Primary100));
        provider.Current.Should().BeNull();
    }

    [Fact]
    public void BeginDraw_with_a_confirmed_snip_persists_to_the_store() => RunOnSta(() =>
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

            var store = new FakeStore(value: null);
            var overlay = new RealWindowOverlay(window);
            var controller = new MapBboxDrawController(
                overlay, store, logger: null, snip: () => new Rect(120, 80, 500, 400));

            controller.BeginDraw();
            DrainDispatcher();

            // Authoritative persistence path (#947): the store holds the snipped rect.
            store.Value.Should().Be(new MapCaptureRectDiu(120, 80, 500, 400));

            // And the overlay was mirrored for visual feedback.
            window.Left.Should().Be(120);
            window.Width.Should().Be(500);
        }
        finally { window.Close(); }
    });

    [Fact]
    public void BeginDraw_with_a_cancelled_snip_does_not_touch_the_store() => RunOnSta(() =>
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

            var store = new FakeStore(value: null);
            var controller = new MapBboxDrawController(
                new RealWindowOverlay(window), store, logger: null, snip: () => null);

            controller.BeginDraw();
            DrainDispatcher();

            store.Value.Should().BeNull("a cancelled snip persists nothing");
            window.Left.Should().Be(33);
        }
        finally { window.Close(); }
    });

    [Fact]
    public void Apply_seam_moves_and_resizes_only_bounds() => RunOnSta(() =>
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

    private sealed class FakeStore : IMapCaptureRectStore
    {
        public FakeStore(MapCaptureRectDiu? value) => Value = value;
        public MapCaptureRectDiu? Value { get; private set; }
        public MapCaptureRectDiu? Get() => Value;
        public void Set(MapCaptureRectDiu rect) => Value = rect;
    }

    private sealed class FakeMonitors : IMonitorDpiProvider
    {
        private readonly IReadOnlyList<MonitorDpiInfo> _monitors;
        public FakeMonitors(params MonitorDpiInfo[] monitors) => _monitors = monitors;
        public IReadOnlyList<MonitorDpiInfo> Monitors() => _monitors;
    }

    /// <summary>A minimal real-window IOverlayWindow for the controller tests.</summary>
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
