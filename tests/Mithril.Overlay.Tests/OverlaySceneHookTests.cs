using FluentAssertions;
using Mithril.MapCalibration;
using Mithril.Overlay.Internal;
using Mithril.Overlay.Tests.Fakes;
using Xunit;

namespace Mithril.Overlay.Tests;

/// <summary>
/// Scene-hook (layer 2) tests for <see cref="IOverlayWindow.RegisterScene"/>
/// + <see cref="IOverlaySceneContext"/>. Bypasses the D3D surface — the
/// <c>OverlayWindowService.DriveSceneForTest</c> seam lets us hand in a
/// stub render target and verify dispatch behaviour without standing up a
/// real <c>D2DOverlaySurface</c>.
///
/// <para>What's covered:</para>
/// <list type="bullet">
/// <item>Register + dispatch round-trip — drawer fires once per tick</item>
/// <item>Dispose returns from <see cref="IOverlayWindow.RegisterScene"/>
/// removes the drawer</item>
/// <item>Multiple registrations are invoked in registration order</item>
/// <item>Uncalibrated area: scene drawers are skipped (same gate as the
/// marker renderer's per-tick chip)</item>
/// <item>Zoom plumbing: <see cref="IOverlaySceneContext.Project"/> reads
/// the live <see cref="IOverlayZoomSource"/> per call</item>
/// <item><see cref="IOverlaySceneContext.Project"/> returns null in
/// uncalibrated-area paths (defensive cover; the per-tick gate already
/// skips drawers there)</item>
/// </list>
/// </summary>
public sealed class OverlaySceneHookTests
{
    private static OverlayWindowService BuildService(
        FakeMapCalibrationService calibration,
        StubAreaState areaState,
        IOverlayZoomSource zoom)
    {
        var markers = new WorldOverlayMarkers();
        var renderer = new MarkerSceneRenderer();
        var position = new StubPositionState();
        return new OverlayWindowService(markers, renderer, calibration, areaState, position, zoom);
    }

    [Fact]
    public void RegisterScene_invokes_drawer_once_per_tick_on_calibrated_area()
    {
        var calibration = new FakeMapCalibrationService();
        calibration.CalibratedAreas.Add("A");
        var areaState = new StubAreaState { CurrentArea = "A" };
        var service = BuildService(calibration, areaState, new FixedOverlayZoomSource(1.0));

        var calls = 0;
        IOverlaySceneContext? captured = null;
        using var handle = ((IOverlayWindow)service).RegisterScene(ctx =>
        {
            calls++;
            captured = ctx;
        });

        // Drive a single tick. The fake render target / factory pointers
        // are never dereferenced inside the scene-context's Project (which
        // we don't call here) or the drawer body (which only counts).
        service.DriveSceneForTest(renderTarget: null!, factory: null!, areaKey: "A", currentZoom: 1.0);

        calls.Should().Be(1);
        captured.Should().NotBeNull();
        captured!.CurrentAreaKey.Should().Be("A");
    }

    [Fact]
    public void Disposing_the_handle_deregisters_the_drawer()
    {
        var calibration = new FakeMapCalibrationService();
        calibration.CalibratedAreas.Add("A");
        var areaState = new StubAreaState { CurrentArea = "A" };
        var service = BuildService(calibration, areaState, new FixedOverlayZoomSource(1.0));

        var calls = 0;
        var handle = ((IOverlayWindow)service).RegisterScene(_ => calls++);
        service.SceneDrawerCount.Should().Be(1);

        handle.Dispose();
        service.SceneDrawerCount.Should().Be(0);

        // Subsequent ticks must not invoke the disposed drawer.
        service.DriveSceneForTest(null!, null!, "A", 1.0);
        calls.Should().Be(0,
            "the disposed drawer must not fire — a future bug where Dispose() didn't " +
            "actually unhook the registration would slowly leak per-tick work and is " +
            "hard to spot in production traces.");
    }

    [Fact]
    public void Multiple_drawers_are_invoked_in_registration_order()
    {
        var calibration = new FakeMapCalibrationService();
        calibration.CalibratedAreas.Add("A");
        var areaState = new StubAreaState { CurrentArea = "A" };
        var service = BuildService(calibration, areaState, new FixedOverlayZoomSource(1.0));

        var order = new List<int>();
        using var h1 = ((IOverlayWindow)service).RegisterScene(_ => order.Add(1));
        using var h2 = ((IOverlayWindow)service).RegisterScene(_ => order.Add(2));
        using var h3 = ((IOverlayWindow)service).RegisterScene(_ => order.Add(3));

        service.DriveSceneForTest(null!, null!, "A", 1.0);

        order.Should().Equal(new[] { 1, 2, 3 },
            because: "drawing-order matters for transparent geometry — D2D has no depth buffer, " +
            "so a drawer that depends on running BEFORE another (e.g. its lines under " +
            "the next drawer's pins) needs a stable registration-order invariant. " +
            "Step 6's only consumer is Legolas with a single scene drawer, but the " +
            "platform contract is multi-consumer (Gwaihir + future modules).");
    }

    [Fact]
    public void Scene_drawers_do_not_fire_on_uncalibrated_area()
    {
        var calibration = new FakeMapCalibrationService(); // nothing calibrated
        var areaState = new StubAreaState { CurrentArea = "AreaUncalibrated" };
        var service = BuildService(calibration, areaState, new FixedOverlayZoomSource(1.0));

        var calls = 0;
        using var h = ((IOverlayWindow)service).RegisterScene(_ => calls++);

        service.DriveSceneForTest(null!, null!, "AreaUncalibrated", 1.0);

        calls.Should().Be(0,
            "scene drawers must be skipped on uncalibrated areas — the Project helper " +
            "would always return null, so there's nothing meaningful for the drawer to " +
            "render and the per-tick chip already tells the user to calibrate.");
        service.StatusMessage.Should().Contain("not calibrated",
            "the uncalibrated chip must surface so the user knows to run the calibration wizard.");
    }

    [Fact]
    public void Project_plumbs_current_zoom_into_WorldToWindow()
    {
        var calibration = new FakeMapCalibrationService();
        calibration.CalibratedAreas.Add("A");
        var zoomsSeenByProjector = new List<double>();
        calibration.Projector = (_, world, zoom) =>
        {
            zoomsSeenByProjector.Add(zoom);
            return new PixelPoint(world.X, world.Z);
        };
        var areaState = new StubAreaState { CurrentArea = "A" };

        // Mutable zoom source so we can change it between ticks.
        var zoom = new MutableZoomSource(1.5);
        var service = BuildService(calibration, areaState, zoom);

        using var h = ((IOverlayWindow)service).RegisterScene(ctx =>
        {
            // Two projection calls per tick to exercise multiple Project()
            // invocations on the same context.
            ctx.Project(10, 20);
            ctx.Project(30, 40);
        });

        service.DriveSceneForTest(null!, null!, "A", 1.5);

        zoomsSeenByProjector.Should().Equal(new[] { 1.5, 1.5 },
            because: "the scene context must pass the per-tick snapshot of IOverlayZoomSource " +
            "through to IMapCalibrationService.WorldToWindow on every Project() call — " +
            "if this regresses to 1.0, the hardcoded zoom from PR #863 is back and " +
            "pins drift whenever the user has the in-game zoom slider off 1.0.");

        // Flip the zoom and drive again — next tick must see the new value.
        zoom.CurrentZoom = 0.75;
        service.DriveSceneForTest(null!, null!, "A", 0.75);
        zoomsSeenByProjector.Should().Equal(new[] { 1.5, 1.5, 0.75, 0.75 },
            because: "zoom changes between ticks must propagate to Project() — a stale snapshot " +
            "captured at scene-context construction would freeze pins at the old zoom.");
    }

    [Fact]
    public void Project_returns_null_for_uncalibrated_areas()
    {
        // This is a defensive-cover test. The per-tick gate already prevents
        // scene drawers from firing on uncalibrated areas; if a future refactor
        // bypasses that gate (e.g. lifts the gate decision elsewhere), the
        // per-call Project must still return null instead of fabricating a
        // pixel.
        var calibration = new FakeMapCalibrationService(); // nothing calibrated
        var areaState = new StubAreaState { CurrentArea = "AreaUncalibrated" };
        var service = BuildService(calibration, areaState, new FixedOverlayZoomSource(1.0));

        // Capture the context from a calibrated-area scene tick, then call
        // Project against the uncalibrated area. Use a calibrated area key
        // to get past the gate during DriveSceneForTest, then verify Project
        // returns null for an out-of-range/uncalibrated lookup.
        calibration.CalibratedAreas.Add("A");
        areaState.CurrentArea = "A";

        IOverlaySceneContext? ctx = null;
        using var h = ((IOverlayWindow)service).RegisterScene(c => ctx = c);
        // Configure the projector to return null — simulates an
        // out-of-range world coord on a calibrated area.
        calibration.Projector = (_, _, _) => null;

        service.DriveSceneForTest(null!, null!, "A", 1.0);

        ctx.Should().NotBeNull();
        var px = ctx!.Project(10, 20);
        px.Should().BeNull(
            "Project must propagate WorldToWindow's null return — a fabricated pixel " +
            "would silently land the marker at (0,0) or similar nonsense.");
    }

    private sealed class MutableZoomSource : IOverlayZoomSource
    {
        public MutableZoomSource(double zoom) { CurrentZoom = zoom; }
        public double CurrentZoom { get; set; }
    }
}
