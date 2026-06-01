using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
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

        int Count(IReadOnlyDictionary<string, IReadOnlyList<TypedDetection>> d, string type) =>
            d.TryGetValue(type, out var l) ? l.Count : 0;
        int asIsLandmarks = Count(asIsDet, "Portal") + Count(asIsDet, "MeditationPillar");
        int alignedLandmarks = Count(alignedDet, "Portal") + Count(alignedDet, "MeditationPillar");

        // Root cause #1 (mithril#938): the live path's Portal/MeditationPillar count
        // is a misalignment-driven false-positive FLOOD — far above the ~15 real
        // refs. Aligning the texture collapses it toward truth. This guards the fix:
        // once AutoCalibrationEngine aligns the inputs, the live (AS-IS-equivalent)
        // count must stop flooding.
        asIsLandmarks.Should().BeGreaterThan(30,
            "the un-aligned live path floods Portal+Pillar with terrain false-positives");
        alignedLandmarks.Should().BeLessThan(asIsLandmarks,
            "aligning the texture cancels terrain and collapses the false-positive flood");
    }

    private void Dump(string label, IReadOnlyDictionary<string, IReadOnlyList<TypedDetection>> det)
    {
        var total = det.Sum(kv => kv.Value.Count);
        var breakdown = string.Join("  ", det
            .OrderBy(kv => kv.Key, StringComparer.Ordinal)
            .Select(kv => $"{kv.Key}={kv.Value.Count}"));
        _out.WriteLine($"  {label}: {total} typed detections [{breakdown}]");
    }
}
