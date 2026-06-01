using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Mithril.MapCalibration;            // WorldCoord
using Mithril.MapCalibration.Detection;
using Mithril.MapCalibration.DependencyInjection;
using Mithril.Tools.MapCalibration.Common;
using Xunit;
using Xunit.Abstractions;
// Disambiguate from the harness lib's own MapRect (Mithril.Tools.MapCalibration.Harness.MapRect).
using DetectionMapRect = Mithril.MapCalibration.Detection.MapRect;

namespace Mithril.Tools.MapCalibration.Harness.Tests;

/// <summary>
/// Offline repro for mithril#938 — the two real Eltibule capture frames (one the
/// live solve rejected at 3 inliers, one it accepted at a bad 7.61&#160;px / 4
/// inliers) replayed through the <b>exact production</b> detect path
/// (<see cref="DeviationBlobCalibrationDetector"/> via
/// <see cref="MapCalibrationServiceCollectionExtensions.AddMithrilMapCalibrationEngine"/>,
/// same TypeFloor 0.80 / RenderSizePx 16 / DeviationFlood / BlobOptions the
/// <c>AutoCalibrationEngine</c> uses).
///
/// <para>It contrasts two detector inputs on the same frame:</para>
/// <list type="bullet">
///   <item><b>AS-IS</b> — what the live engine actually feeds: the full uncropped
///   screenshot vs the full 2048-wide base texture. <see cref="LocalNccDeviation"/>
///   is told the screenshot's dimensions, so the wider texture is read at the wrong
///   stride and top-left-cropped — terrain never cancels in the deviation map.</item>
///   <item><b>ALIGNED</b> — the detector's documented contract (screenshot cropped
///   to the located <see cref="MapRect"/> + base texture resampled to that frame),
///   so terrain cancels and only the added icons survive.</item>
/// </list>
///
/// <para>The base texture + icon templates are read from the local
/// <c>%LocalAppData%/Mithril/assets</c> cache the sidecar populates; the test
/// skips when that cache (or a frame fixture) is absent, so it stays a green no-op
/// in CI and a live diagnostic on a developer machine.</para>
/// </summary>
public sealed class EltibuleLiveFrameDetectionRepro
{
    private const string Area = "AreaEltibule";

    private static readonly string AssetCacheDir = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "Mithril", "assets");

    private static string FrameDir => Path.Combine(AppContext.BaseDirectory, "Fixtures");

    // Production constants, mirrored from AutoCalibrationEngine.
    private const double LowNcc = 0.5;
    private const double TypeFloor = 0.80;
    private const int RenderSizePx = 16;
    private static readonly BlobOptions BlobOpts =
        new(MinArea: 12, MaxIconArea: 900, MinSolidity: 0.35, MaxAspect: 2.5, MinPeak: 0.7);

    private readonly ITestOutputHelper _out;
    public EltibuleLiveFrameDetectionRepro(ITestOutputHelper output) => _out = output;

    [SkippableTheory]
    [InlineData("eltibule-frame1-rejected-3inliers.gray.png")]
    [InlineData("eltibule-frame2-accepted-7.61px.gray.png")]
    public void Detection_asis_vs_aligned(string frameFile)
    {
        var framePath = Path.Combine(FrameDir, frameFile);
        Skip.IfNot(File.Exists(framePath), $"frame fixture missing: {framePath}");
        Skip.IfNot(File.Exists(Path.Combine(AssetCacheDir, $"map-texture-{Area}.bin")),
            $"base-texture cache missing under {AssetCacheDir} — calibrate once to populate it");

        using var sp = new ServiceCollection()
            // Capture the engine's logs (incl. the inlier-correspondence line) to test output.
            .AddSingleton<ILoggerFactory>(new TestOutputLoggerFactory(_out))
            // pgVersion null → hash gate accepts-with-warn, matching the live run.
            .AddMithrilMapCalibrationEngine(AssetCacheDir)
            .BuildServiceProvider();

        var baseTex = sp.GetRequiredService<IBaseTextureProvider>().TryGetBaseTexture(Area);
        Skip.If(baseTex is null, "base texture failed to load from cache");
        var templates = sp.GetRequiredService<IIconTemplateProvider>().GetTemplates();
        var detector = sp.GetRequiredService<ICalibrationDetector>();

        var frame = ImageIo.LoadGray(framePath);
        _out.WriteLine($"frame {frameFile}: {frame.Width}x{frame.Height} | baseTex {baseTex!.Width}x{baseTex.Height} | templates {templates.Templates.Count}");

        // Use the production downsampled refiner path (#972 — DefaultWorkingLongEdgePx),
        // NOT the full-res 2-arg overload (that's the pre-#972 minutes-long brute force).
        var mapRect = MapRectLocator.AutoDetect(frame, baseTex, LowNcc, MapRectLocator.DefaultWorkingLongEdgePx);
        Skip.If(mapRect is null, "map sub-rect not located");
        _out.WriteLine(
            $"MapRect origin=({mapRect!.OriginX},{mapRect.OriginY}) {mapRect.Width}x{mapRect.Height} " +
            $"tex={mapRect.TextureWidth}x{mapRect.TextureHeight} score={mapRect.AutoDetectScore:0.000} scaleF={mapRect.SourceScaleFactor:0.000}");

        // AS-IS — exactly what AutoCalibrationEngine passes today.
        var asIs = new DetectionRequest(
            frame, baseTex, mapRect, templates, RimMaskMode.DeviationFlood, LowNcc, TypeFloor, BlobOpts)
        { RenderSizePx = RenderSizePx };
        var asIsDet = detector.Detect(asIs);
        Dump("AS-IS  (uncropped shot + raw 2048-wide texture)", asIsDet);

        // ALIGNED — the detector's contract: crop the shot to the located sub-rect,
        // resample the texture to that same size so the two are pixel-aligned. The
        // rect carries the FULL texture dims so crop-space anchors still scale into
        // texture space for a downstream solve. (The production fix warps the other
        // way — texture INTO full-screenshot space — to keep anchors in the frame
        // the solver's ScreenshotToTexture expects; for detection counts the
        // direction is immaterial since the detector ignores MapRect.)
        var crop = ImageOps.Crop(frame, mapRect.OriginX, mapRect.OriginY, mapRect.Width, mapRect.Height);
        var alignedTex = ImageOps.Resize(baseTex, mapRect.Width, mapRect.Height);
        var alignedRect = new DetectionMapRect(0, 0, mapRect.Width, mapRect.Height, mapRect.TextureWidth, mapRect.TextureHeight);
        var aligned = new DetectionRequest(
            crop, alignedTex, alignedRect, templates, RimMaskMode.DeviationFlood, LowNcc, TypeFloor, BlobOpts)
        { RenderSizePx = RenderSizePx };
        var alignedDet = detector.Detect(aligned);
        Dump("ALIGNED (cropped shot + resampled texture)", alignedDet);

        // Solve layer — run the production engine (detector + gate, both
        // orientations) on each input set and report inliers / residual. This is
        // the symptom of record: AS-IS should reproduce the live solve (frame1
        // rejected at 3 inliers, frame2 accepted at ~7.61px/4); ALIGNED shows what
        // the input-alignment fix buys.
        var engine = sp.GetRequiredService<MapCalibrationSolveEngine>();
        var refs = EltibuleReferences();
        DumpSolve("AS-IS  solve", engine.Solve(asIs, refs));
        DumpSolve("ALIGNED solve", engine.Solve(aligned, refs));

        int Count(IReadOnlyDictionary<string, IReadOnlyList<TypedDetection>> d, string type) =>
            d.TryGetValue(type, out var l) ? l.Count : 0;
        int asIsLandmarks = Count(asIsDet, "Portal") + Count(asIsDet, "MeditationPillar");
        int alignedLandmarks = Count(alignedDet, "Portal") + Count(alignedDet, "MeditationPillar");

        // The live (AS-IS) path's Portal/MeditationPillar count is a
        // misalignment-driven false-positive FLOOD — far above the ~15 real refs —
        // because the detector compares the screenshot against a mis-strided texture
        // (it ignores request.MapRect) so terrain never cancels.
        //
        // IMPORTANT (mithril#938): this flood is NOT the bad-solve's root cause.
        // RANSAC tolerates the false positives — AS-IS still reproduces the live
        // solve EXACTLY (see DumpSolve). Naively aligning (crop+resample) collapses
        // the count toward truth here yet makes the SOLVE WORSE (the DumpSolve above
        // shows ALIGNED dropping to 2 inliers) because the coarse resample displaces
        // true-positioned icons. So this assertion documents the flood as a detection
        // artifact ONLY; it must NOT be read as "alignment fixes the solve."
        asIsLandmarks.Should().BeGreaterThan(30,
            "the un-aligned live path floods Portal+Pillar with terrain false-positives");
        alignedLandmarks.Should().BeLessThan(asIsLandmarks,
            "the coarse align trims the count — but DumpSolve shows it does NOT improve the solve");
    }

    private void Dump(string label, IReadOnlyDictionary<string, IReadOnlyList<TypedDetection>> det)
    {
        var total = det.Sum(kv => kv.Value.Count);
        var breakdown = string.Join("  ", det
            .OrderBy(kv => kv.Key, StringComparer.Ordinal)
            .Select(kv => $"{kv.Key}={kv.Value.Count}"));
        _out.WriteLine($"  {label}: {total} typed detections [{breakdown}]");
    }

    private void DumpSolve(string label, CalibrationSolveResult r)
    {
        if (r.Calibration is not null)
            _out.WriteLine($"  {label}: ACCEPTED — {r.InlierCount} inliers, residual {r.Calibration.ResidualPixels:0.00} px");
        else
            _out.WriteLine($"  {label}: REJECTED — {r.InlierCount} inliers ({r.RejectReason})");
    }

    /// <summary>
    /// The 38 AreaEltibule references the live <c>ReferenceDataAreaReferenceProvider</c>
    /// emits (v470): 9 Portal + 6 MeditationPillar + 6 TeleportationPlatform + 17 Npc.
    /// World coords are (X, Y, Z); the solver pairs on X/Z. The 2 positionless npcs.json
    /// entries (Work Orders sign, Sacrificial Bowl pedestal) are intentionally absent.
    /// </summary>
    private static List<LandmarkReference> EltibuleReferences()
    {
        static LandmarkReference R(string type, string name, double x, double y, double z) =>
            new(type, name, new WorldCoord(x, y, z));

        return new List<LandmarkReference>
        {
            // Portals
            R("Portal", "Strange Gateway", 954.409973, 93.550003, 437.089996),
            R("Portal", "Lord Eltibule's Residence", 1138.161865, 39.093884, 1367.77417),
            R("Portal", "Hogan's Keep", 1495.542236, 113.689079, 336.063507),
            R("Portal", "Cellar Entrance", 1138.047729, 39.093884, 1349.43811),
            R("Portal", "Travel", 1167.875, 129.005005, 2924.259033),
            R("Portal", "Employees Only", 2438.050049, 112.720001, 2614.22998),
            R("Portal", "Boarded Up Entrance", 2108.312988, 40.327, 2104.249023),
            R("Portal", "Crypt Entrance", 1099.73999, 48.342999, 1505.755981),
            R("Portal", "Travel to Kur Mountains", 1517.650024, 114.507004, 272.23999),
            // Meditation pillars (__15 / __16 are co-located in the data)
            R("MeditationPillar", "Meditation Pillar", 2408.116455, 128.816833, 2021.867554),
            R("MeditationPillar", "Meditation Pillar", 1562.388428, 112.367661, 424.236877),
            R("MeditationPillar", "Meditation Pillar", 1562.388428, 112.367661, 424.236877),
            R("MeditationPillar", "Meditation Pillar", 1934.636841, 37.967136, 1308.01709),
            R("MeditationPillar", "Meditation Pillar", 941.062805, 27.910137, 1543.284912),
            R("MeditationPillar", "Meditation Pillar", 916.844788, 96.698303, 2428.760254),
            // Teleportation platforms
            R("TeleportationPlatform", "TeleportCircle_PlateauAlt", 2334.690186, 135.734726, 841.390625),
            R("TeleportationPlatform", "TeleportCircle_SieAntry", 1977.883789, 41.054588, 1373.350098),
            R("TeleportationPlatform", "TeleportCircle_Courtyard", 1114.727173, 39.016926, 1355.878052),
            R("TeleportationPlatform", "TeleportCircle_PlateauCity", 604.247681, 134.018173, 1494.122192),
            R("TeleportationPlatform", "TeleportCircle_AbandonedCourtyard", 1519.629761, 113.419998, 398.076965),
            R("TeleportationPlatform", "TeleportCircle_BFE1", 2550.061768, 1.430984, 1352.015747),
            // NPCs
            R("Npc", "Braigon", 1099.952026, 37.634277, 1398.786987),
            R("Npc", "George Madler", 1107.27002, 37.634277, 1397.030029),
            R("Npc", "Gretchen Salas", 1151.800049, 41.096237, 1277.800049),
            R("Npc", "Helena Veilmoor", 1584.709961, 111.967621, 473.345245),
            R("Npc", "Hogan", 1531.617432, 111.967621, 441.754822),
            R("Npc", "Jesina", 1939.183594, 39.621506, 1361.98999),
            R("Npc", "Jumjab", 1988.909424, 196.989639, 470.373993),
            R("Npc", "Kalaba", 1068.28894, 38.251373, 1335.883057),
            R("Npc", "Kleave", 1083.234985, 37.650242, 1332.546021),
            R("Npc", "Mythander", 1580.430054, 111.967621, 430.109985),
            R("Npc", "Oritania", 1121.492798, 37.634277, 1340.76416),
            R("Npc", "Percy Evans", 439.309998, 48.147507, 632.599976),
            R("Npc", "Sie Antry", 1944.559814, 39.621506, 1367.099365),
            R("Npc", "Suspicious Cow", 1181.699951, 31.682209, 1167.290039),
            R("Npc", "Thimble Pete", 1526.0, 111.959999, 458.029999),
            R("Npc", "Yasinda", 1580.857056, 111.958, 390.020996),
            R("Npc", "Yetta", 1089.330444, 37.641918, 1388.192139),
        };
    }

    /// <summary>Routes engine ILogger output (incl. the #938 inlier-correspondence line) to test output.</summary>
    private sealed class TestOutputLoggerFactory : ILoggerFactory
    {
        private readonly ITestOutputHelper _out;
        public TestOutputLoggerFactory(ITestOutputHelper output) => _out = output;
        public void AddProvider(ILoggerProvider provider) { }
        public ILogger CreateLogger(string categoryName) => new Logger(_out, categoryName);
        public void Dispose() { }

        private sealed class Logger : ILogger
        {
            private readonly ITestOutputHelper _out;
            private readonly string _cat;
            public Logger(ITestOutputHelper output, string cat) { _out = output; _cat = cat; }
            public IDisposable BeginScope<TState>(TState state) where TState : notnull => NullScope.Instance;
            public bool IsEnabled(LogLevel logLevel) => true;
            public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
                => _out.WriteLine($"    [{_cat}] {formatter(state, exception)}");
            private sealed class NullScope : IDisposable
            {
                public static readonly NullScope Instance = new();
                public void Dispose() { }
            }
        }
    }
}
