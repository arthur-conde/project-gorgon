using FluentAssertions;
using Legolas.Domain;
using Legolas.Rendering;
using Mithril.MapCalibration;
using Mithril.Overlay;
using Mithril.Overlay.Internal;
using Xunit;
using Color = System.Windows.Media.Color;
using Color4 = Vortice.Mathematics.Color4;

namespace Legolas.Tests.Rendering;

/// <summary>
/// Byte-parity snapshot tests for the #835-step-2 marker drawers. Each test
/// builds a deterministic <see cref="PinScene"/> fixture, renders it via the
/// existing <see cref="PinSceneRenderer"/> to a baseline PNG, then builds the
/// equivalent marker list + style set, renders via the new
/// <see cref="MarkerSceneRenderer"/> + Legolas drawers to a second PNG, and
/// asserts the two PNG byte streams are identical.
///
/// <para><b>Dual role for these tests.</b> This PR (step 2) uses them as a
/// parity guard &#8212; PinSceneRenderer is the source of truth and the new
/// drawers must reproduce it. Step 6 will delete PinSceneRenderer; the same
/// tests then become regression guards against the checked-in baseline PNGs,
/// catching any later drift in the new drawer implementation.</para>
///
/// <para><b>Headless CI gating.</b>
/// <see cref="HeadlessD2DRenderTarget.TryCreate"/> attempts hardware D3D11
/// first, then WARP. If both fail, the test is skipped with a Skip-string
/// explaining the gating (per scaffold PR #850's note that headless CI may
/// reject D3D11 device creation entirely). The same gating applies to every
/// snapshot test in this class; they share the helper.</para>
///
/// <para><b>Calibration drawer absent.</b> Per the issue body for #835,
/// today's calibration markers are drawn by a WPF <c>ItemsControl</c>
/// (per the <c>#495</c> comment in <see cref="PinSceneRenderer"/>), not by
/// the D2D renderer. There is no <c>PinSceneRenderer</c> branch to
/// byte-parity-compare against, so the calibration drawer ships in step 5
/// when the calibration markers switch over to D2D.</para>
/// </summary>
public sealed class LegolasMarkerDrawerSnapshotTests
{
    private const int CanvasWidth = 240;
    private const int CanvasHeight = 240;

    /// <summary>
    /// Skip message for the headless-D3D11 path. Kept as a constant so the
    /// "what" and "why" of a skipped run is obvious in the test report.
    /// </summary>
    private const string SkipNoD3D =
        "No usable D3D11 driver (neither Hardware nor WARP). Expected on truly headless CI "
        + "with no graphics drivers installed; tests run locally + on CI images that ship the "
        + "WARP software adapter. Not a silent skip — the gate is explicit; see comment in source.";

    private static readonly Color CyanFill = Color.FromArgb(0xFF, 0x00, 0xFF, 0xFF);
    private static readonly Color WhiteStroke = Color.FromArgb(0xFF, 0xFF, 0xFF, 0xFF);
    private static readonly Color GoldStroke = Color.FromArgb(0xFF, 0xFF, 0xD2, 0x3F);
    private static readonly Color Green = Color.FromArgb(0xFF, 0x4C, 0xAF, 0x50);
    private static readonly Color RouteColor = Color.FromArgb(0xFF, 0xFF, 0xFF, 0xFF);

    private static PinLayerStyle SurveyOuterStyle() => new(
        Shape: PinShape.Circle,
        FillColor: Color.FromArgb(1, 0, 0, 0), // near-transparent fill, matches default
        StrokeColor: CyanFill,
        StrokeStyle: PinStrokeStyle.Dashed,
        StrokeThickness: 2.0,
        Size: 0);

    private static PinLayerStyle SurveyCenterStyle() => new(
        Shape: PinShape.Circle,
        FillColor: CyanFill,
        StrokeColor: Color.FromArgb(0, 0, 0, 0),
        StrokeStyle: PinStrokeStyle.None,
        StrokeThickness: 0,
        Size: 5.0);

    private static PinLayerStyle PlayerOuterStyle() => new(
        Shape: PinShape.Circle,
        FillColor: Color.FromArgb(0, 0, 0, 0),
        StrokeColor: WhiteStroke,
        StrokeStyle: PinStrokeStyle.Solid,
        StrokeThickness: 2.0,
        Size: 18.0);

    private static PinLayerStyle PlayerCenterStyle() => new(
        Shape: PinShape.Square,
        FillColor: Green,
        StrokeColor: Color.FromArgb(0, 0, 0, 0),
        StrokeStyle: PinStrokeStyle.None,
        StrokeThickness: 0,
        Size: 2.0);

    [Fact]
    public void Survey_pin_default_style_byte_matches_PinSceneRenderer()
    {
        RunSnapshotComparison(
            fixtureName: "survey_default",
            scene: BuildSceneWithSinglePin(activeIndex: null, treatment: null),
            markers: new (PixelPoint, IMarkerStyle)[]
            {
                (new PixelPoint(120, 120), new LegolasSurveyMarkerStyle(
                    Outer: SurveyOuterStyle(),
                    Center: SurveyCenterStyle(),
                    OuterDiameter: 24.0,
                    ActiveTreatment: null)),
            });
    }

    [Theory]
    [InlineData(ActivePinTreatment.Halo)]
    [InlineData(ActivePinTreatment.Glow)]
    [InlineData(ActivePinTreatment.ScaleUp)]
    [InlineData(ActivePinTreatment.FillSwap)]
    public void Survey_active_pin_byte_matches_PinSceneRenderer(ActivePinTreatment treatment)
    {
        var spec = new ActivePinTreatmentSpec(
            Treatment: treatment,
            Color: WhiteStroke,
            HaloPaddingPx: 3.0,
            StrokeThickness: 2.0,
            GlowBlurRadius: 12.0);
        RunSnapshotComparison(
            fixtureName: "survey_active_" + treatment.ToString().ToLowerInvariant(),
            scene: BuildSceneWithSinglePin(activeIndex: 0, treatment: spec),
            markers: new (PixelPoint, IMarkerStyle)[]
            {
                (new PixelPoint(120, 120), new LegolasSurveyMarkerStyle(
                    Outer: SurveyOuterStyle(),
                    Center: SurveyCenterStyle(),
                    OuterDiameter: 24.0,
                    ActiveTreatment: spec)),
            });
    }

    [Fact]
    public void Motherlode_pin_byte_matches_PinSceneRenderer()
    {
        RunSnapshotComparison(
            fixtureName: "motherlode_pin",
            scene: BuildSceneWithMotherlodePin(includeGuidance: false),
            markers: new (PixelPoint, IMarkerStyle)[]
            {
                (new PixelPoint(120, 120), new LegolasMotherlodeMarkerStyle(
                    Outer: SurveyOuterStyle(),
                    Center: SurveyCenterStyle(),
                    OuterDiameter: 24.0)),
            });
    }

    [Fact]
    public void Motherlode_guidance_circle_byte_matches_PinSceneRenderer()
    {
        RunSnapshotComparison(
            fixtureName: "motherlode_guidance",
            scene: BuildSceneWithMotherlodeGuidance(),
            markers: new (PixelPoint, IMarkerStyle)[]
            {
                (new PixelPoint(120, 120),
                 new LegolasMotherlodeGuidanceMarkerStyle(RadiusPixels: 60.0, StrokeColor: GoldStroke)),
            });
    }

    [Fact]
    public void Player_anchor_byte_matches_PinSceneRenderer()
    {
        RunSnapshotComparison(
            fixtureName: "player_anchor",
            scene: BuildSceneWithPlayerAnchor(),
            markers: new (PixelPoint, IMarkerStyle)[]
            {
                (new PixelPoint(120, 120), new LegolasPlayerMarkerStyle(
                    Outer: PlayerOuterStyle(),
                    Center: PlayerCenterStyle())),
            });
    }

    // -----------------------------------------------------------------
    // Scene builders
    // -----------------------------------------------------------------

    private static PinScene BuildSceneWithSinglePin(int? activeIndex, ActivePinTreatmentSpec? treatment) =>
        new(
            RoutePoints: Array.Empty<PixelPoint>(),
            ActiveSegmentPoints: Array.Empty<PixelPoint>(),
            Wedges: Array.Empty<WedgeArc>(),
            SurveyPins: new[] { new PixelPoint(120, 120) },
            MotherlodePins: Array.Empty<PixelPoint>(),
            MotherlodeGuidance: null,
            ActivePinIndex: activeIndex,
            ActiveTreatment: treatment,
            SurveyOuter: SurveyOuterStyle(),
            SurveyCenter: SurveyCenterStyle(),
            SurveyOuterDiameter: 24.0,
            PlayerPosition: null,
            PlayerOuter: PlayerOuterStyle(),
            PlayerCenter: PlayerCenterStyle(),
            RouteLineColor: RouteColor,
            WedgeFillColor: Color.FromArgb(0x40, 0xFF, 0xFF, 0xFF),
            WedgeStrokeColor: WhiteStroke,
            ActiveSegmentDashOffset: 0);

    private static PinScene BuildSceneWithMotherlodePin(bool includeGuidance) =>
        new(
            RoutePoints: Array.Empty<PixelPoint>(),
            ActiveSegmentPoints: Array.Empty<PixelPoint>(),
            Wedges: Array.Empty<WedgeArc>(),
            SurveyPins: Array.Empty<PixelPoint>(),
            MotherlodePins: new[] { new PixelPoint(120, 120) },
            MotherlodeGuidance: includeGuidance
                ? new MotherlodeGuidanceCircle(new PixelPoint(120, 120), 60.0, GoldStroke)
                : null,
            ActivePinIndex: null,
            ActiveTreatment: null,
            SurveyOuter: SurveyOuterStyle(),
            SurveyCenter: SurveyCenterStyle(),
            SurveyOuterDiameter: 24.0,
            PlayerPosition: null,
            PlayerOuter: PlayerOuterStyle(),
            PlayerCenter: PlayerCenterStyle(),
            RouteLineColor: RouteColor,
            WedgeFillColor: Color.FromArgb(0x40, 0xFF, 0xFF, 0xFF),
            WedgeStrokeColor: WhiteStroke,
            ActiveSegmentDashOffset: 0);

    private static PinScene BuildSceneWithMotherlodeGuidance() =>
        new(
            RoutePoints: Array.Empty<PixelPoint>(),
            ActiveSegmentPoints: Array.Empty<PixelPoint>(),
            Wedges: Array.Empty<WedgeArc>(),
            SurveyPins: Array.Empty<PixelPoint>(),
            MotherlodePins: Array.Empty<PixelPoint>(),
            MotherlodeGuidance: new MotherlodeGuidanceCircle(new PixelPoint(120, 120), 60.0, GoldStroke),
            ActivePinIndex: null,
            ActiveTreatment: null,
            SurveyOuter: SurveyOuterStyle(),
            SurveyCenter: SurveyCenterStyle(),
            SurveyOuterDiameter: 24.0,
            PlayerPosition: null,
            PlayerOuter: PlayerOuterStyle(),
            PlayerCenter: PlayerCenterStyle(),
            RouteLineColor: RouteColor,
            WedgeFillColor: Color.FromArgb(0x40, 0xFF, 0xFF, 0xFF),
            WedgeStrokeColor: WhiteStroke,
            ActiveSegmentDashOffset: 0);

    private static PinScene BuildSceneWithPlayerAnchor() =>
        new(
            RoutePoints: Array.Empty<PixelPoint>(),
            ActiveSegmentPoints: Array.Empty<PixelPoint>(),
            Wedges: Array.Empty<WedgeArc>(),
            SurveyPins: Array.Empty<PixelPoint>(),
            MotherlodePins: Array.Empty<PixelPoint>(),
            MotherlodeGuidance: null,
            ActivePinIndex: null,
            ActiveTreatment: null,
            SurveyOuter: SurveyOuterStyle(),
            SurveyCenter: SurveyCenterStyle(),
            SurveyOuterDiameter: 24.0,
            PlayerPosition: new PixelPoint(120, 120),
            PlayerOuter: PlayerOuterStyle(),
            PlayerCenter: PlayerCenterStyle(),
            RouteLineColor: RouteColor,
            WedgeFillColor: Color.FromArgb(0x40, 0xFF, 0xFF, 0xFF),
            WedgeStrokeColor: WhiteStroke,
            ActiveSegmentDashOffset: 0);

    // -----------------------------------------------------------------
    // Render comparison core
    // -----------------------------------------------------------------

    private static void RunSnapshotComparison(
        string fixtureName,
        PinScene scene,
        IReadOnlyList<(PixelPoint, IMarkerStyle)> markers)
    {
        // Try to build a headless D2D RT (hardware D3D11 then WARP). If both
        // fail (truly headless CI without graphics drivers) the test surfaces
        // a loud failure with the diagnostic in the message — per the
        // #835-step-2 brief, silent skip is forbidden. Once the project's CI
        // images are confirmed to ship WARP, this gate can be relaxed; if
        // they don't, the test class should be moved behind a
        // <c>[Fact(Skip = SkipNoD3D)]</c> attribute at compile time and the
        // PR body should document the gating explicitly. Today the dev box
        // path is the one we care about and it has hardware D3D11.
        using var baselineRt = HeadlessD2DRenderTarget.TryCreate(CanvasWidth, CanvasHeight);
        baselineRt.Should().NotBeNull(SkipNoD3D);

        var freshBaselinePng = RenderBaselinePng(baselineRt!, scene);

        // Persist the baseline PNG as a checked-in fixture (per the brief —
        // step 6 deletes PinSceneRenderer, so the baseline has to exist
        // independently of the legacy renderer source). If the on-disk
        // baseline doesn't exist yet, write it; if it does, prefer it as the
        // golden master so we catch any later drift in PinSceneRenderer
        // (during the migration window) and in the new drawer (post-step-6).
        var baselinePath = ResolveBaselinePath(fixtureName);
        var baselinePng = LoadOrPersistBaseline(baselinePath, freshBaselinePng);

        // Belt-and-braces: when the baseline is loaded from disk, also verify
        // that PinSceneRenderer still produces the same bytes today. If it
        // drifts (someone changes PinSceneRenderer during the migration
        // window) we want to catch it; the new drawer is meant to track the
        // legacy renderer until step 6.
        if (!ReferenceEquals(baselinePng, freshBaselinePng))
        {
            freshBaselinePng.Should().Equal(baselinePng,
                "PinSceneRenderer's output drifted from the checked-in baseline at "
                + baselinePath + " — if this is intentional (e.g. a bug fix in the "
                + "legacy renderer during the migration window), regenerate the "
                + "baseline by deleting the file and re-running this test.");
        }

        using var newRt = HeadlessD2DRenderTarget.TryCreate(CanvasWidth, CanvasHeight);
        newRt.Should().NotBeNull(SkipNoD3D);
        var newPng = RenderNewPipelinePng(newRt!, markers);

        newPng.Should().Equal(baselinePng,
            "the new marker drawer must produce byte-identical D2D output to "
            + "the legacy PinSceneRenderer; this is the parity guarantee that lets "
            + "step 6 retire PinSceneRenderer without visual regression. "
            + "Snapshot test also serves post-step-6 as regression guard against the "
            + "checked-in baseline at " + baselinePath
            + " (see class-level docs).");
    }

    /// <summary>
    /// Resolve the absolute path to the checked-in baseline PNG for the
    /// given fixture. The build copies fixtures next to the test DLL, but
    /// the source-tree path is the canonical location — we read from the
    /// source tree so a regeneration (delete + rerun) updates the
    /// checked-in copy directly. <c>AppContext.BaseDirectory</c> is
    /// <c>bin/Debug/net10.0-windows/</c>, four levels deep relative to the
    /// repo's <c>tests/Legolas.Tests/Rendering/Snapshots/Baselines/</c>.
    /// </summary>
    private static string ResolveBaselinePath(string fixtureName)
    {
        var dllDir = AppContext.BaseDirectory;
        // bin/Debug/net10.0-windows/ -> ../../../Rendering/Snapshots/Baselines/
        var baselineDir = System.IO.Path.GetFullPath(
            System.IO.Path.Combine(dllDir, "..", "..", "..", "Rendering", "Snapshots", "Baselines"));
        System.IO.Directory.CreateDirectory(baselineDir);
        return System.IO.Path.Combine(baselineDir, fixtureName + ".png");
    }

    /// <summary>
    /// Load the baseline PNG from disk if it exists; otherwise persist the
    /// supplied fresh render as the baseline and return it. Returning the
    /// fresh bytes when no on-disk baseline was found lets the caller skip
    /// the redundant "PinSceneRenderer drifted" check on the first run.
    /// </summary>
    private static byte[] LoadOrPersistBaseline(string path, byte[] freshBaselinePng)
    {
        if (System.IO.File.Exists(path))
        {
            return System.IO.File.ReadAllBytes(path);
        }
        System.IO.File.WriteAllBytes(path, freshBaselinePng);
        return freshBaselinePng;
    }

    private static byte[] RenderBaselinePng(HeadlessD2DRenderTarget rt, PinScene scene)
    {
        using var brushes = new D2DBrushCache();
        brushes.Bind(rt.RenderTarget);
        rt.RenderTarget.BeginDraw();
        rt.RenderTarget.Clear(new Color4(0, 0, 0, 0));
        PinSceneRenderer.Render(scene, rt.RenderTarget, rt.Factory, brushes);
        rt.RenderTarget.EndDraw();
        return rt.EncodePng();
    }

    private static byte[] RenderNewPipelinePng(
        HeadlessD2DRenderTarget rt,
        IReadOnlyList<(PixelPoint, IMarkerStyle)> markers)
    {
        var sceneRenderer = new MarkerSceneRenderer();
        LegolasOverlayDrawerRegistrations.RegisterAll(sceneRenderer);

        using var brushes = new D2DBrushCache();
        brushes.Bind(rt.RenderTarget);
        rt.RenderTarget.BeginDraw();
        rt.RenderTarget.Clear(new Color4(0, 0, 0, 0));
        sceneRenderer.Render(markers, rt.RenderTarget, rt.Factory, brushes);
        rt.RenderTarget.EndDraw();
        return rt.EncodePng();
    }

}
