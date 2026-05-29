using FluentAssertions;
using Legolas.Domain;
using Legolas.Flow;
using Legolas.Rendering;
using Legolas.Services;
using Legolas.Tests.Rendering;
using Legolas.ViewModels;
using Microsoft.Extensions.Logging.Abstractions;
using Mithril.MapCalibration;
using Mithril.Overlay;
using Mithril.Overlay.Internal;
using Color4 = Vortice.Mathematics.Color4;

namespace Legolas.Tests.ViewModels;

/// <summary>
/// Producer-side iteration-2 regression test (#835 review iteration 2 — B3).
///
/// <para><b>What this asserts beyond the rest of the snapshot suite.</b>
/// <see cref="Legolas.Tests.Rendering.MarkerPipelineSnapshotTests"/> calls
/// <see cref="WorldOverlayMarkers.AddMarker"/> directly with hand-crafted
/// styles and explicit insertion order — it doesn't exercise the
/// <see cref="MapOverlayViewModel"/> producer at all. A bug in
/// <c>RefreshAllSurveyMarkers</c> that registers Surveys in source order
/// (rather than "non-active first, then active last" per
/// <c>PinSceneRenderer.DrawSurveyPins</c>'s contract) would pass every
/// existing test in the suite — the renderer iterates whatever insertion
/// order the registry hands it, and the hand-crafted tests build the right
/// one by hand.</para>
///
/// <para>This test constructs a real <see cref="MapOverlayViewModel"/> with
/// a real <see cref="WorldOverlayMarkers"/> registry, seeds three surveys
/// (active at the middle index 1, deliberately not the last), triggers the
/// VM's selection-driven re-registration path, and asserts both
/// <list type="number">
/// <item>the registry's insertion order has the active marker LAST (the
/// invariant the renderer + step 6's PinSceneRenderer-retirement plan
/// depend on), and</item>
/// <item>the render of the projected snapshot byte-matches PR #853's
/// checked-in <c>multi_marker_survey</c> baseline — proving the end-to-end
/// production path (settings → BuildSurveyMarkerStyle → AddMarker →
/// projection driver → MarkerSceneRenderer) produces the same bytes as the
/// hand-crafted control.</item>
/// </list></para>
///
/// <para>The settings overrides below tune <c>LegolasSettings</c> to match
/// the hand-crafted fixture's outer/center/diameter exactly so the
/// byte-compare is meaningful. The active-treatment defaults
/// (Halo / WhiteStroke / 3.0 / 2.0 / 12.0) already match the fixture
/// out-of-the-box.</para>
/// </summary>
public sealed class MapOverlayMarkerRegistrationOrderTests
{
    private const int CanvasWidth = 240;
    private const int CanvasHeight = 240;
    private const string AreaKey = "AreaTest";
    private const string SkipNoD3DPrefix =
        "No usable D3D11 driver (neither Hardware nor WARP). Inner driver error: ";

    [Fact]
    public void RefreshAllSurveyMarkers_places_active_pin_last_in_insertion_order()
    {
        // No D3D needed — this is a pure insertion-order assertion. The
        // byte-compare test below covers the render side.
        var (session, registry, selected, _) = BuildSutWithThreeSurveys();

        // Sanity: middle survey is the one selected. If it ends up last
        // in CurrentAreaMarkers, the fix is in place; if it ends up source-
        // order-last (120, 180), the bug from iteration-1 reproduces.
        session.SelectedSurvey = selected;

        var snapshot = registry.CurrentAreaMarkers;
        snapshot.Should().HaveCount(3,
            "all three seeded survey markers must reach the registry — a producer that " +
            "silently drops the selected/active one would let this fail and the iteration-2 " +
            "ordering bug hide behind the drop.");

        // The last entry in the snapshot must be the active one. We can't
        // identify it by handle (opaque), so we identify by world coord:
        // selected survey was seeded at world (120, 180) at source index 1.
        var lastWorld = snapshot[^1].World;
        lastWorld.X.Should().Be(120,
            "PinSceneRenderer.DrawSurveyPins renders the active pin LAST so its halo " +
            "sits on top of neighbouring pins. MarkerSceneRenderer iterates insertion " +
            "order with no special active handling, so the registry's insertion-order " +
            "tail MUST be the active marker. Iteration-1 of #835 registered surveys " +
            "in source order — middle-selected meant middle-rendered, halo occluded. " +
            "Selected was seeded at world (120, 180) at source index 1; without the " +
            "fix, the last entry would be the source-order-last (160, 100).");
        lastWorld.Z.Should().Be(180,
            "the selected survey was seeded at (120, 180); a different Z here means " +
            "the wrong survey ended up last, which is the same ordering bug.");
    }

    [SkippableFact]
    public void Producer_path_byte_matches_multi_marker_survey_baseline()
    {
        using var rt = HeadlessD2DRenderTarget.TryCreate(
            CanvasWidth, CanvasHeight, out var driverError);
        Skip.If(rt is null, SkipNoD3DPrefix + (driverError?.Message ?? "(unknown)"));

        var (session, registry, selected, _) = BuildSutWithThreeSurveys();
        session.SelectedSurvey = selected;

        var snapshot = registry.CurrentAreaMarkers;
        snapshot.Should().HaveCount(3, "producer must register every survey.");

        // Identity calibration so the seeded world coords (X, _, Z) double
        // as the rendered pixels — matches the multi_marker_survey baseline's
        // pixel layout exactly.
        var calibration = new IdentityCalibrationService();
        var projected = OverlayWindowService.ProjectMarkers(
            snapshot, AreaKey, calibration, currentZoom: 1.0);
        projected.Should().HaveCount(3,
            "identity calibration must project every snapshot entry — drops here would " +
            "mask a producer/projection bug.");

        var renderer = new MarkerSceneRenderer();
        LegolasOverlayDrawerRegistrations.RegisterAll(renderer);

        using var brushes = new D2DBrushCache();
        brushes.Bind(rt!.RenderTarget);
        rt.RenderTarget.BeginDraw();
        rt.RenderTarget.Clear(new Color4(0, 0, 0, 0));
        renderer.Render(projected, rt.RenderTarget, rt.Factory, brushes);
        rt.RenderTarget.EndDraw();
        var producerPng = rt.EncodePng();

        var baselinePath = ResolveBaselinePath("multi_marker_survey");
        System.IO.File.Exists(baselinePath).Should().BeTrue(
            "PR #853 must have checked in multi_marker_survey.png at " + baselinePath + ".");
        var baselinePng = System.IO.File.ReadAllBytes(baselinePath);

        producerPng.Should().Equal(baselinePng,
            "the end-to-end producer pipeline (settings -> BuildSurveyMarkerStyle -> " +
            "AddMarker -> CurrentAreaMarkers -> ProjectMarkers -> MarkerSceneRenderer) " +
            "must produce the same bytes as the hand-crafted multi_marker_survey fixture. " +
            "If this fails, either (a) the iteration-2 active-last ordering fix regressed " +
            "or (b) BuildSurveyMarkerStyle's settings -> PinLayerStyle mapping drifted.");
    }

    /// <summary>Build the system under test: a real <see cref="MapOverlayViewModel"/>
    /// with a real <see cref="WorldOverlayMarkers"/> and minimal area-state
    /// stub. Three surveys seeded at world (80, 100), (120, 180), (160, 100)
    /// — the active one is index 1 (middle) at (120, 180), which (a)
    /// matches the existing <c>multi_marker_survey</c> baseline's active-pin
    /// pixel layout for the byte-compare test below and (b) deliberately
    /// places the selected pin in the middle of the
    /// <see cref="SessionState.Surveys"/> source order so the iteration-1
    /// ordering bug is reachable — under the bug, the source-order-last
    /// (160, 100) would land at the registry tail instead of the selected
    /// (120, 180).</summary>
    private static (SessionState session, WorldOverlayMarkers registry, SurveyItemViewModel selected, MapOverlayViewModel map)
        BuildSutWithThreeSurveys()
    {
        var session = new SessionState();
        var settings = new LegolasSettings
        {
            // Diameter = 2 * 12.0 = 24.0, matching the multi_marker_survey
            // fixture's hand-crafted OuterDiameter.
            SurveyPinRadiusMetres = 12.0,
        };
        // Match the hand-crafted multi_marker_survey fixture's PinLayerStyle
        // values exactly. The LegolasPinShapeStyle defaults are very close
        // (cyan dashed outer, circle centre) but the centre fill + centre
        // stroke diverge: fixture uses CyanFill centre + transparent centre
        // stroke (None, 0); settings defaults use #01000000 fill + cyan
        // stroke at thickness 2.0.
        settings.PinStyle.Center.FillColor = "#FF00FFFF";
        settings.PinStyle.Center.StrokeColor = "#00000000";
        settings.PinStyle.Center.StrokeStyle = PinStrokeStyle.None;
        settings.PinStyle.Center.StrokeThickness = 0;
        // Outer + active-pin defaults already match the fixture (Halo,
        // WhiteStroke #FFFFFFFF, HaloPadding 3.0, HaloThickness 2.0,
        // GlowBlurRadius 12.0, dashed outer with cyan stroke).

        var surveyFlow = new SurveyFlowController(session, settings);
        // SurveyFlowController defaults to Listening, which is the state
        // BuildSurveyMarkerStyle treats the SelectedSurvey as active in.

        var optimizer = new AdaptiveRouteOptimizer(new HeldKarpOptimizer(), new NearestNeighbourTwoOptOptimizer());
        var projector = new CoordinateProjector();
        var brushes = new LegolasBrushes(settings);
        var registry = new WorldOverlayMarkers(NullLogger.Instance) { CurrentArea = AreaKey };
        var areaState = new StubAreaState(AreaKey);

        var map = new MapOverlayViewModel(
            session, projector, optimizer, surveyFlow, brushes, settings,
            pinCalibration: null, positionState: null, bus: null,
            areaCalibration: null, motherlode: null, characterPin: null,
            markers: registry, areaState: areaState);

        // Three surveys. World coords cover the same three pixel positions
        // the multi_marker_survey baseline uses under identity calibration,
        // but ordered so the active one is the MIDDLE of the source order
        // (not the last). The fix's post-condition: registry insertion order
        // places (120, 180) — the selected — at the tail, matching the
        // baseline's "active-last" rendering exactly.
        var s0 = SeedSurvey(session, world: (80, 100), name: "p0");
        var selected = SeedSurvey(session, world: (120, 180), name: "p_active");
        var s2 = SeedSurvey(session, world: (160, 100), name: "p2");

        // Hold on to s0/s2 for symmetry — they're observable through the
        // session.Surveys collection too, the locals just make the intent
        // self-documenting.
        _ = (s0, s2);

        return (session, registry, selected, map);
    }

    private static SurveyItemViewModel SeedSurvey(
        SessionState session, (double X, double Z) world, string name)
    {
        // CreateAbsolute stamps Survey.World + a placeholder pixel. The
        // VM's RegisterSurveyMarker reads Model.World (not the pixel) and
        // the projection driver re-projects through the calibration, so the
        // pixel placeholder doesn't affect the rendered output.
        var pixelPlaceholder = new PixelPoint(world.X, world.Z);
        var w = new WorldCoord(world.X, 0, world.Z);
        var model = Survey.CreateAbsolute(name, w, pixelPlaceholder, gridIndex: 0);
        var vm = new SurveyItemViewModel(model);
        session.Surveys.Add(vm);
        return vm;
    }

    private static string ResolveBaselinePath(string fixtureName)
    {
        var dllDir = AppContext.BaseDirectory;
        var baselineDir = System.IO.Path.GetFullPath(
            System.IO.Path.Combine(dllDir, "..", "..", "..", "Rendering", "Snapshots", "Baselines"));
        return System.IO.Path.Combine(baselineDir, fixtureName + ".png");
    }

    private sealed class StubAreaState : Arda.World.Player.IAreaState
    {
        public StubAreaState(string area) { CurrentArea = area; }
        public string? CurrentArea { get; }
    }

    private sealed class IdentityCalibrationService : IMapCalibrationService
    {
        public bool IsCalibrated(string areaKey) => true;
        public PixelPoint? WorldToWindow(string areaKey, WorldCoord world, double currentZoom)
            => new PixelPoint(world.X, world.Z);
        public WorldCoord? WindowToWorld(string areaKey, PixelPoint pixel, double currentZoom)
            => new WorldCoord(pixel.X, 0, pixel.Y);
        public AreaCalibration? GetCalibration(string areaKey) => null;
        public IReadOnlyDictionary<string, AreaCalibration> AllCalibrations
            => new Dictionary<string, AreaCalibration>();
        public IReadOnlyList<AreaCalibration> GetAllSources(string areaKey)
            => Array.Empty<AreaCalibration>();
        public void SaveUserRefinement(string areaKey, AreaCalibration calibration) { }
        public void ClearUserRefinement(string areaKey) { }
        public int ImportUserRefinements(IReadOnlyDictionary<string, AreaCalibration> source) => 0;
        public event EventHandler<string>? Changed { add { } remove { } }
    }
}
