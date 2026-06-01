using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Arda.World.Player;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Mithril.MapCalibration;
using Mithril.MapCalibration.Capture;
using Mithril.MapCalibration.Detection;
using Mithril.MapCalibration.DependencyInjection;
using Mithril.Tools.MapCalibration.Common;
using Xunit;
using Xunit.Abstractions;
using DetectionMapRect = Mithril.MapCalibration.Detection.MapRect;

namespace Mithril.Tools.MapCalibration.Harness.Tests;

/// <summary>
/// #978 acceptance suite for the precise screenshot↔texture ECC registration fix.
/// Drives the PRODUCTION seam (<see cref="TextureRegistrationRefiner"/>) and the full
/// <see cref="AutoCalibrationEngine"/> path — not the hand-rolled probe — against the
/// committed Eltibule frame fixtures + the live asset cache.
///
/// <para>The probe (<see cref="OpenCvRegistrationProbe.Ecc_registration_vs_ground_truth"/>)
/// proves the ECC recipe at the algorithm level; these tests prove the production
/// wiring (refiner → aligned crop/resample → engine) actually clears the gate.
/// SkippableFact: a missing fixture or asset cache skips (green no-op in CI), but on
/// this machine the cache is present so they EXECUTE and must PASS.</para>
/// </summary>
public sealed class EccRegistrationRefinerTests
{
    private const string Area = "AreaEltibule";

    private static readonly string AssetCacheDir = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Mithril", "assets");
    private static string FrameDir => Path.Combine(AppContext.BaseDirectory, "Fixtures");
    private const string Frame2 = "eltibule-frame2-accepted-7.61px.gray.png";

    // frame 2 ground-truth map bbox (hand-verified, mirrored across the #938 suite).
    private const int Gt2X = 130, Gt2Y = 60, Gt2W = 995, Gt2H = 986;

    private readonly ITestOutputHelper _out;
    public EccRegistrationRefinerTests(ITestOutputHelper output) => _out = output;

    /// <summary>
    /// Test 1 (#978): the PRODUCTION <see cref="TextureRegistrationRefiner"/> — not the
    /// probe's hand-rolled call — drives a coarse seed → ECC refine to within ~3px
    /// (x/y) / ~4px (w) of the hand-verified frame-2 ground-truth rect.
    /// </summary>
    [SkippableFact]
    public void Production_refiner_reaches_ground_truth_on_frame2()
    {
        var (frame, baseTex) = LoadFrameAndTexture(Frame2);

        var rect = new TextureRegistrationRefiner().Refine(frame, baseTex, 0.5);
        rect.Should().NotBeNull("the coarse locator finds a seed on frame 2");

        _out.WriteLine(
            $"production refiner: ({rect!.OriginX},{rect.OriginY}) {rect.Width}x{rect.Height}  " +
            $"gt ({Gt2X},{Gt2Y}) {Gt2W}x{Gt2H}  " +
            $"delta=({rect.OriginX - Gt2X},{rect.OriginY - Gt2Y},{rect.Width - Gt2W},{rect.Height - Gt2H})");

        Math.Abs(rect.OriginX - Gt2X).Should().BeLessThanOrEqualTo(3);
        Math.Abs(rect.OriginY - Gt2Y).Should().BeLessThanOrEqualTo(3);
        Math.Abs(rect.Width - Gt2W).Should().BeLessThanOrEqualTo(4);
        rect.TextureWidth.Should().Be(baseTex.Width);
        rect.TextureHeight.Should().Be(baseTex.Height);
    }

    /// <summary>
    /// Test 2 (#978): a non-convergent input (all-black frame → no terrain to align)
    /// must fail-soft — the refiner returns the coarse seed and NO
    /// <see cref="OpenCvSharp.OpenCVException"/> escapes. Uses the real base texture so
    /// the coarse locator still produces a seed to fall back to.
    /// </summary>
    [SkippableFact]
    public void Fail_soft_returns_coarse_rect_without_throwing()
    {
        Skip.IfNot(File.Exists(Path.Combine(AssetCacheDir, $"map-texture-{Area}.bin")),
            $"base-texture cache missing under {AssetCacheDir}");
        using var sp = new ServiceCollection().AddMithrilMapCalibrationEngine(AssetCacheDir).BuildServiceProvider();
        var baseTex = sp.GetRequiredService<IBaseTextureProvider>().TryGetBaseTexture(Area);
        Skip.If(baseTex is null, "base texture failed to load");

        // A uniform (all-black) frame the size of a real capture: the coarse locator
        // still picks SOME seed, but ECC on a flat image has no gradient to align and
        // throws OpenCVException → the refiner must swallow it and return the seed.
        var black = new GrayImage(1257, 1049, new byte[1257 * 1049]);

        var refiner = new TextureRegistrationRefiner();
        DetectionMapRect? rect = null;
        var act = () => rect = refiner.Refine(black, baseTex!, 0.5);

        act.Should().NotThrow("ECC non-convergence must be swallowed (fail-soft to the coarse rect)");
        // Whatever the coarse locator returned (possibly null on a flat frame) is fine;
        // the contract under test is "no throw". If a seed was found it must echo the
        // texture dims (i.e. it's the coarse rect, not a half-built ECC rect).
        if (rect is not null)
        {
            rect.TextureWidth.Should().Be(baseTex!.Width);
            rect.TextureHeight.Should().Be(baseTex.Height);
        }
    }

    /// <summary>
    /// Test 3 (#978) — THE acceptance gate. Drive the committed frame 2 through the full
    /// <see cref="AutoCalibrationEngine.TryCalibrateCurrentAreaAsync"/> with the REAL
    /// refiner / base-texture provider / icon templates / solver+detector+gate, fakes
    /// only for the capture/window/region/persist seams. Assert the outcome persisted
    /// with ≥ 8 inliers / ≤ 2px residual (today: 4 inliers / 7.61px).
    /// </summary>
    [SkippableFact]
    public async Task Full_engine_auto_calibrates_frame2_at_8plus_inliers_under_2px()
    {
        var (frame, _) = LoadFrameAndTexture(Frame2);

        using var sp = new ServiceCollection().AddMithrilMapCalibrationEngine(AssetCacheDir).BuildServiceProvider();
        var baseTextures = sp.GetRequiredService<IBaseTextureProvider>();
        Skip.If(baseTextures.TryGetBaseTexture(Area) is null, "base texture failed to load");
        var iconTemplates = sp.GetRequiredService<IIconTemplateProvider>();
        var solveEngine = sp.GetRequiredService<MapCalibrationSolveEngine>();

        var capturedService = new CapturingCalibrationService();
        var engine = new AutoCalibrationEngine(
            areaState: new StubAreaState(Area),
            windowLocator: new StubWindowLocator(new GameWindow(1, new CaptureRect(0, 0, 1920, 1080))),
            region: new StubRegionProvider(new CaptureRect(0, 0, frame.Width, frame.Height)),
            capture: new StubCapture(frame),
            refiner: new TextureRegistrationRefiner(),                 // REAL ECC seam
            baseTextures: baseTextures,                                // REAL cache provider
            references: new StubAreaRefs(EltibuleLiveFrameDetectionRepro.EltibuleReferences()),
            solver: new MapCalibrationSolveEngineAdapter(solveEngine), // REAL detect→solve→gate
            iconTemplates: iconTemplates,                              // REAL cache templates
            calibrationService: capturedService,
            logger: null);

        var outcome = await engine.TryCalibrateCurrentAreaAsync(CancellationToken.None);

        capturedService.Saved.TryGetValue(Area, out var cal);
        // The persisted AreaCalibration.ReferenceCount is the inlier count of the
        // final refit (LandmarkCalibrationSolver feeds the inlier set as references).
        _out.WriteLine(
            $"frame2 full-engine: persisted={outcome.Persisted} reason={outcome.RejectReason} " +
            $"inliers={(cal is not null ? cal.ReferenceCount.ToString() : "-")} " +
            $"residual={(cal is not null ? cal.ResidualPixels.ToString("0.00") : "-")}px");

        outcome.Persisted.Should().BeTrue($"frame 2 must auto-calibrate (reason: {outcome.RejectReason})");
        cal.Should().NotBeNull();
        cal!.Source.Should().Be(CalibrationSource.AutoCapture);
        cal.ReferenceCount.Should().BeGreaterThanOrEqualTo(8, "the ECC-aligned inputs must clear the ≥8 inlier acceptance bar");
        cal.ResidualPixels.Should().BeLessThanOrEqualTo(2.0, "the ECC-aligned inputs must clear the ≤2px acceptance bar");
    }

    // ── helpers ──────────────────────────────────────────────────────────────

    private (GrayImage Frame, GrayImage BaseTex) LoadFrameAndTexture(string frameFile)
    {
        var framePath = Path.Combine(FrameDir, frameFile);
        Skip.IfNot(File.Exists(framePath), $"frame fixture missing: {framePath}");
        Skip.IfNot(File.Exists(Path.Combine(AssetCacheDir, $"map-texture-{Area}.bin")),
            $"base-texture cache missing under {AssetCacheDir}");
        using var sp = new ServiceCollection().AddMithrilMapCalibrationEngine(AssetCacheDir).BuildServiceProvider();
        var baseTex = sp.GetRequiredService<IBaseTextureProvider>().TryGetBaseTexture(Area);
        Skip.If(baseTex is null, "base texture failed to load from cache");
        var frame = ImageIo.LoadGray(framePath);
        return (frame, baseTex!);
    }

    private sealed class StubAreaState(string? area) : IAreaState
    {
        public string? CurrentArea { get; } = area;
    }

    private sealed class StubWindowLocator(GameWindow? window) : IGameWindowLocator
    {
        private readonly GameWindow? _window = window;
        public GameWindow? Locate() => _window;
    }

    private sealed class StubRegionProvider(CaptureRect? current) : IMapCaptureRegionProvider
    {
        public CaptureRect? Current { get; } = current;
    }

    private sealed class StubCapture(GrayImage frame) : ICaptureService
    {
        private readonly GrayImage _frame = frame;
        public Task<GrayImage?> CaptureMapAsync(CaptureRect bbox, CancellationToken ct) => Task.FromResult<GrayImage?>(_frame);
    }

    private sealed class StubAreaRefs(IReadOnlyList<LandmarkReference> refs) : IAreaReferenceProvider
    {
        private readonly IReadOnlyList<LandmarkReference> _refs = refs;
        public IReadOnlyList<LandmarkReference> ForArea(string areaKey) => _refs;
    }

    /// <summary>Captures the persisted calibration so the test can read inliers + residual.</summary>
    private sealed class CapturingCalibrationService : IMapCalibrationService
    {
        public Dictionary<string, AreaCalibration> Saved { get; } = new(StringComparer.Ordinal);
        public bool IsCalibrated(string areaKey) => Saved.ContainsKey(areaKey);
        public PixelPoint? WorldToWindow(string areaKey, WorldCoord world, double currentZoom) => null;
        public WorldCoord? WindowToWorld(string areaKey, PixelPoint pixel, double currentZoom) => null;
        public AreaCalibration? GetCalibration(string areaKey) => Saved.TryGetValue(areaKey, out var c) ? c : null;
        public IReadOnlyDictionary<string, AreaCalibration> AllCalibrations => Saved;
        public IReadOnlyList<AreaCalibration> GetAllSources(string areaKey) => Array.Empty<AreaCalibration>();
        public void SaveUserRefinement(string areaKey, AreaCalibration calibration) => Saved[areaKey] = calibration;
        public void ClearUserRefinement(string areaKey) => Saved.Remove(areaKey);
        public int ImportUserRefinements(IReadOnlyDictionary<string, AreaCalibration> source) => 0;
        public event EventHandler<string>? Changed { add { } remove { } }
    }
}
