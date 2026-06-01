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

    /// <summary>
    /// Renders the detector's deviation pipeline to PNGs for BOTH frames at their
    /// pixel-perfect bboxes (so registration is held good and we see the detection
    /// input the zoom level produces): the raw deviation map, the dev>=threshold
    /// foreground, the edge-connected "DeviationFlood" rim mask, and the post-mask
    /// blob input. Saved under %LocalAppData%/Mithril/diagnostics/calibration/938-masks/.
    /// </summary>
    [SkippableTheory]
    [InlineData("eltibule-frame1-rejected-3inliers.gray.png", "frame1", 204, 133, 847, 841)]
    [InlineData("eltibule-frame2-accepted-7.61px.gray.png", "frame2", 130, 60, 995, 986)]
    public void Dump_deviation_masks(string frameFile, string prefix, int bx, int by, int bw, int bh)
    {
        var framePath = Path.Combine(FrameDir, frameFile);
        Skip.IfNot(File.Exists(framePath), $"frame fixture missing: {framePath}");
        Skip.IfNot(File.Exists(Path.Combine(AssetCacheDir, $"map-texture-{Area}.bin")),
            $"base-texture cache missing under {AssetCacheDir}");

        using var sp = new ServiceCollection().AddMithrilMapCalibrationEngine(AssetCacheDir).BuildServiceProvider();
        var baseTex = sp.GetRequiredService<IBaseTextureProvider>().TryGetBaseTexture(Area);
        Skip.If(baseTex is null, "base texture failed to load");
        var frame = ImageIo.LoadGray(framePath);

        var outDir = Path.Combine(AssetCacheDir, "..", "diagnostics", "calibration", "938-masks");
        Directory.CreateDirectory(outDir);

        // Aligned at the pixel-perfect bbox (good registration held constant across frames).
        var crop = ImageOps.Crop(frame, bx, by, bw, bh);
        var alignedTex = ImageOps.Resize(baseTex!, bw, bh);
        RenderMasks(prefix, "aligned", crop, alignedTex, outDir);
        _out.WriteLine($"{prefix} ({bw}x{bh}) deviation masks written to {Path.GetFullPath(outDir)}");
    }

    private void RenderMasks(string framePrefix, string tag, GrayImage shot, GrayImage tex, string outDir)
    {
        int w = shot.Width, h = shot.Height, n = w * h;
        var dev = LocalNccDeviation.DeviationMap(
            LocalNccDeviation.ToGrayFloat(shot), LocalNccDeviation.ToGrayFloat(tex),
            w, h, win: 11, out _, addedOnly: true);

        // 1) raw deviation map (0..1 -> 0..255)
        var devPx = new byte[n];
        for (int i = 0; i < n; i++) devPx[i] = (byte)Math.Clamp(dev[i] * 255.0, 0, 255);
        ImageIo.SaveGrayPng(new GrayImage(w, h, devPx), Path.Combine(outDir, $"{framePrefix}-{tag}-1-deviation.png"));

        // 2) foreground: dev >= 1-LowNcc (the detector's threshold)
        double devThr = 1.0 - LowNcc;
        var fg = new bool[n];
        for (int i = 0; i < n; i++) fg[i] = dev[i] >= devThr;
        SaveMask(fg, w, h, Path.Combine(outDir, $"{framePrefix}-{tag}-2-foreground.png"));

        // 3) edge-connected DeviationFlood rim mask (replicates DeviationBlobDetector)
        var rim = new bool[n];
        var q = new Queue<int>();
        void Enq(int x, int y)
        {
            if (x < 0 || x >= w || y < 0 || y >= h) return;
            int k = y * w + x;
            if (fg[k] && !rim[k]) { rim[k] = true; q.Enqueue(k); }
        }
        for (int x = 0; x < w; x++) { Enq(x, 0); Enq(x, h - 1); }
        for (int y = 0; y < h; y++) { Enq(0, y); Enq(w - 1, y); }
        while (q.Count > 0)
        {
            int k = q.Dequeue();
            Enq(k % w - 1, k / w); Enq(k % w + 1, k / w); Enq(k % w, k / w - 1); Enq(k % w, k / w + 1);
        }
        SaveMask(rim, w, h, Path.Combine(outDir, $"{framePrefix}-{tag}-3-rimflood.png"));

        // 4) post-mask blob input: foreground with the rim removed
        var clean = new bool[n];
        for (int i = 0; i < n; i++) clean[i] = fg[i] && !rim[i];
        SaveMask(clean, w, h, Path.Combine(outDir, $"{framePrefix}-{tag}-4-blobinput.png"));

        int fgCount = fg.Count(b => b), rimCount = rim.Count(b => b), cleanCount = clean.Count(b => b);
        _out.WriteLine($"  {tag} ({w}x{h}): foreground={fgCount} ({100.0*fgCount/n:0.0}%)  rimflood={rimCount}  blobinput={cleanCount} ({100.0*cleanCount/n:0.0}%)");
    }

    private static void SaveMask(bool[] mask, int w, int h, string path)
    {
        var px = new byte[w * h];
        for (int i = 0; i < px.Length; i++) px[i] = mask[i] ? (byte)255 : (byte)0;
        ImageIo.SaveGrayPng(new GrayImage(w, h, px), path);
    }

    /// <summary>
    /// Reports the located MapRect bbox per frame + how well the crop+resampled
    /// texture actually register (deviation meanNcc — 1.0 = perfect terrain cancel,
    /// lower = misregistered). Saves the crop and the resampled texture so the
    /// registration can be eyeballed.
    /// </summary>
    [SkippableFact]
    public void Dump_crop_accuracy()
    {
        Skip.IfNot(File.Exists(Path.Combine(AssetCacheDir, $"map-texture-{Area}.bin")),
            $"base-texture cache missing under {AssetCacheDir}");
        using var sp = new ServiceCollection().AddMithrilMapCalibrationEngine(AssetCacheDir).BuildServiceProvider();
        var baseTex = sp.GetRequiredService<IBaseTextureProvider>().TryGetBaseTexture(Area);
        Skip.If(baseTex is null, "base texture failed to load");
        var outDir = Path.Combine(AssetCacheDir, "..", "diagnostics", "calibration", "938-masks");
        Directory.CreateDirectory(outDir);

        foreach (var (file, label) in new[]
        {
            ("eltibule-frame1-rejected-3inliers.gray.png", "frame1"),
            ("eltibule-frame2-accepted-7.61px.gray.png", "frame2"),
        })
        {
            var path = Path.Combine(FrameDir, file);
            if (!File.Exists(path)) continue;
            var frame = ImageIo.LoadGray(path);
            var r = MapRectLocator.AutoDetect(frame, baseTex!, LowNcc, MapRectLocator.DefaultWorkingLongEdgePx);
            if (r is null) { _out.WriteLine($"{label}: no rect"); continue; }

            var crop = ImageOps.Crop(frame, r.OriginX, r.OriginY, r.Width, r.Height);
            var tex = ImageOps.Resize(baseTex!, r.Width, r.Height);
            LocalNccDeviation.DeviationMap(
                LocalNccDeviation.ToGrayFloat(crop), LocalNccDeviation.ToGrayFloat(tex),
                r.Width, r.Height, win: 11, out var meanNcc, addedOnly: false);

            _out.WriteLine(
                $"{label}: bbox=({r.OriginX},{r.OriginY}) {r.Width}x{r.Height}  " +
                $"[right={r.OriginX + r.Width}, bottom={r.OriginY + r.Height}]  of {frame.Width}x{frame.Height}  " +
                $"score={r.AutoDetectScore:0.000} scaleF={r.SourceScaleFactor:0.000}  " +
                $"crop↔texture meanNCC={meanNcc:0.000} (1.0=perfect register)");

            ImageIo.SaveGrayPng(crop, Path.Combine(outDir, $"{label}-crop.png"));
            ImageIo.SaveGrayPng(tex, Path.Combine(outDir, $"{label}-texture-resampled.png"));
        }
    }

    /// <summary>
    /// Tests whether the poor crop↔texture registration is caused by the base
    /// texture's decorative frame (terrain inset within an ornate border the in-game
    /// crop lacks). Strips a border of each candidate fraction off the texture,
    /// re-runs the locator, and reports the registration score + crop↔texture
    /// meanNCC. A clear peak at some inset ⇒ frame-inset is the cause (fixable);
    /// flat-and-low everywhere ⇒ the in-game render and asset texture are
    /// fundamentally dissimilar (a deeper problem for the deviation approach).
    /// </summary>
    [SkippableFact]
    public void Frame_inset_registration_sweep_frame2()
    {
        var framePath = Path.Combine(FrameDir, "eltibule-frame2-accepted-7.61px.gray.png");
        Skip.IfNot(File.Exists(framePath), $"frame fixture missing: {framePath}");
        Skip.IfNot(File.Exists(Path.Combine(AssetCacheDir, $"map-texture-{Area}.bin")),
            $"base-texture cache missing under {AssetCacheDir}");
        using var sp = new ServiceCollection().AddMithrilMapCalibrationEngine(AssetCacheDir).BuildServiceProvider();
        var baseTex = sp.GetRequiredService<IBaseTextureProvider>().TryGetBaseTexture(Area);
        Skip.If(baseTex is null, "base texture failed to load");
        var frame = ImageIo.LoadGray(framePath);

        foreach (var inset in new[] { 0.0, 0.04, 0.06, 0.08, 0.10, 0.12, 0.15 })
        {
            int mx = (int)Math.Round(baseTex!.Width * inset);
            int my = (int)Math.Round(baseTex.Height * inset);
            var tex = inset == 0.0
                ? baseTex
                : ImageOps.Crop(baseTex, mx, my, baseTex.Width - 2 * mx, baseTex.Height - 2 * my);

            var r = MapRectLocator.AutoDetect(frame, tex, 0.0, MapRectLocator.DefaultWorkingLongEdgePx);
            if (r is null) { _out.WriteLine($"inset {inset:0.00}: no rect"); continue; }

            var crop = ImageOps.Crop(frame, r.OriginX, r.OriginY, r.Width, r.Height);
            var texR = ImageOps.Resize(tex, r.Width, r.Height);
            LocalNccDeviation.DeviationMap(
                LocalNccDeviation.ToGrayFloat(crop), LocalNccDeviation.ToGrayFloat(texR),
                r.Width, r.Height, win: 11, out var meanNcc, addedOnly: false);

            _out.WriteLine(
                $"inset {inset:0.00} (tex {tex.Width}x{tex.Height}): " +
                $"bbox=({r.OriginX},{r.OriginY}) {r.Width}x{r.Height}  score={r.AutoDetectScore:0.000}  meanNCC={meanNcc:0.000}");
        }
    }

    /// <summary>
    /// Since the base texture and the in-game render don't pixel-register (the
    /// deviation premise is weak), test the alternative: the
    /// <see cref="WholeImageTemplateDetector"/>, which NCC-matches icon glyphs
    /// directly over the screenshot and needs no terrain cancellation. Runs both
    /// detectors through the production engine+gate on frame 2 AS-IS and compares
    /// detection counts + solve inliers/residual.
    /// </summary>
    [SkippableFact]
    public void Detector_comparison_frame2()
    {
        var framePath = Path.Combine(FrameDir, "eltibule-frame2-accepted-7.61px.gray.png");
        Skip.IfNot(File.Exists(framePath), $"frame fixture missing: {framePath}");
        Skip.IfNot(File.Exists(Path.Combine(AssetCacheDir, $"map-texture-{Area}.bin")),
            $"base-texture cache missing under {AssetCacheDir}");
        using var sp = new ServiceCollection().AddMithrilMapCalibrationEngine(AssetCacheDir).BuildServiceProvider();
        var baseTex = sp.GetRequiredService<IBaseTextureProvider>().TryGetBaseTexture(Area);
        Skip.If(baseTex is null, "base texture failed to load");
        var templates = sp.GetRequiredService<IIconTemplateProvider>().GetTemplates();
        var frame = ImageIo.LoadGray(framePath);
        var mapRect = MapRectLocator.AutoDetect(frame, baseTex!, LowNcc, MapRectLocator.DefaultWorkingLongEdgePx);
        Skip.If(mapRect is null, "map sub-rect not located");
        var refs = EltibuleReferences();

        var request = new DetectionRequest(
            frame, baseTex!, mapRect!, templates, RimMaskMode.DeviationFlood, LowNcc, TypeFloor, BlobOpts)
        { RenderSizePx = RenderSizePx };

        foreach (var (name, det) in new (string, ICalibrationDetector)[]
        {
            ("DeviationBlob (production)", new DeviationBlobCalibrationDetector()),
            ("WholeImageTemplate", new WholeImageTemplateDetector()),
        })
        {
            Dump($"{name} detect", det.Detect(request));
            var engine = new MapCalibrationSolveEngine(det, new CalibrationConfidenceGate());
            DumpSolve($"{name} solve", engine.Solve(request, refs));
        }
    }

    /// <summary>
    /// Runs detect→solve with a MANUALLY-supplied map bbox (via InlineData) instead
    /// of the auto-located MapRect, to test whether an accurate screenshot↔texture
    /// rect improves the solve. The bbox bounds the full rocky-edged map in
    /// screenshot pixels; TextureWidth/Height stay the full 2048x2033. Runs AS-IS
    /// (detector ignores the rect; the solver's ScreenshotToTexture uses it) and the
    /// aligned crop, and reports crop↔texture meanNCC + inlier correspondences.
    /// PLACEHOLDER bboxes are the auto-located values — replace with manual ones.
    /// </summary>
    [SkippableTheory]
    [InlineData("eltibule-frame1-rejected-3inliers.gray.png", 204, 133, 847, 841)]   // manual bbox (122726)
    [InlineData("eltibule-frame2-accepted-7.61px.gray.png", 130, 60, 995, 986)]      // manual bbox (123012)
    public void Manual_bbox_solve(string frameFile, int bx, int by, int bw, int bh)
    {
        var framePath = Path.Combine(FrameDir, frameFile);
        Skip.IfNot(File.Exists(framePath), $"frame fixture missing: {framePath}");
        Skip.IfNot(File.Exists(Path.Combine(AssetCacheDir, $"map-texture-{Area}.bin")),
            $"base-texture cache missing under {AssetCacheDir}");

        using var sp = new ServiceCollection()
            .AddSingleton<ILoggerFactory>(new TestOutputLoggerFactory(_out))
            .AddMithrilMapCalibrationEngine(AssetCacheDir)
            .BuildServiceProvider();
        var baseTex = sp.GetRequiredService<IBaseTextureProvider>().TryGetBaseTexture(Area);
        Skip.If(baseTex is null, "base texture failed to load");
        var templates = sp.GetRequiredService<IIconTemplateProvider>().GetTemplates();
        var detector = sp.GetRequiredService<ICalibrationDetector>();
        var engine = sp.GetRequiredService<MapCalibrationSolveEngine>();
        var refs = EltibuleReferences();
        var frame = ImageIo.LoadGray(framePath);

        var manual = new DetectionMapRect(bx, by, bw, bh, baseTex!.Width, baseTex.Height);
        _out.WriteLine($"{frameFile}: manual bbox ({bx},{by}) {bw}x{bh}  of {frame.Width}x{frame.Height}");

        // AS-IS detection (full frame) + solve using the manual rect to map anchors->texture.
        var asIs = new DetectionRequest(frame, baseTex, manual, templates,
            RimMaskMode.DeviationFlood, LowNcc, TypeFloor, BlobOpts) { RenderSizePx = RenderSizePx };
        Dump("AS-IS detect", detector.Detect(asIs));
        DumpSolve("AS-IS solve (manual rect)", engine.Solve(asIs, refs));

        // Aligned crop to the manual bbox + resampled texture; report registration quality.
        var crop = ImageOps.Crop(frame, bx, by, bw, bh);
        var tex = ImageOps.Resize(baseTex, bw, bh);
        LocalNccDeviation.DeviationMap(
            LocalNccDeviation.ToGrayFloat(crop), LocalNccDeviation.ToGrayFloat(tex),
            bw, bh, win: 11, out var meanNcc, addedOnly: false);
        _out.WriteLine($"  crop↔texture meanNCC={meanNcc:0.000} (1.0=perfect register)");
        var alignedRect = new DetectionMapRect(0, 0, bw, bh, baseTex.Width, baseTex.Height);
        var aligned = new DetectionRequest(crop, tex, alignedRect, templates,
            RimMaskMode.DeviationFlood, LowNcc, TypeFloor, BlobOpts) { RenderSizePx = RenderSizePx };
        Dump("ALIGNED detect", detector.Detect(aligned));
        DumpSolve("ALIGNED solve", engine.Solve(aligned, refs));
    }

    /// <summary>
    /// Frame 1 rescue probe: refine the manual bbox by a local offset+scale search
    /// that maximizes crop↔texture meanNCC, then solve the aligned crop with a
    /// PERMISSIVE gate (floor 2, residual 1000px) so every inlier correspondence
    /// prints — to see whether frame 1 is bbox-limited (a better rect → ≥4 inliers)
    /// or genuinely icon-poor (a zoomed-in capture with too few distinct landmarks).
    /// </summary>
    [SkippableFact]
    public void Frame1_rescue_attempt()
    {
        var framePath = Path.Combine(FrameDir, "eltibule-frame1-rejected-3inliers.gray.png");
        Skip.IfNot(File.Exists(framePath), $"frame fixture missing: {framePath}");
        Skip.IfNot(File.Exists(Path.Combine(AssetCacheDir, $"map-texture-{Area}.bin")),
            $"base-texture cache missing under {AssetCacheDir}");
        using var sp = new ServiceCollection()
            .AddSingleton<ILoggerFactory>(new TestOutputLoggerFactory(_out))
            .AddMithrilMapCalibrationEngine(AssetCacheDir).BuildServiceProvider();
        var baseTex = sp.GetRequiredService<IBaseTextureProvider>().TryGetBaseTexture(Area);
        Skip.If(baseTex is null, "base texture failed to load");
        var templates = sp.GetRequiredService<IIconTemplateProvider>().GetTemplates();
        var detector = sp.GetRequiredService<ICalibrationDetector>();
        var refs = EltibuleReferences();
        var frame = ImageIo.LoadGray(framePath);

        // Manual bbox from the user.
        int bx0 = 204, by0 = 133, bw0 = 847, bh0 = 841;

        // Local search: maximize crop↔texture meanNCC over small offset + scale.
        double bestMean = -1; int bbx = bx0, bby = by0, bbw = bw0, bbh = bh0;
        foreach (var s in new[] { 0.96, 0.98, 0.99, 1.0, 1.01, 1.02, 1.04 })
            foreach (var dx in new[] { -16, -8, 0, 8, 16 })
                foreach (var dy in new[] { -16, -8, 0, 8, 16 })
                {
                    int w = (int)Math.Round(bw0 * s), h = (int)Math.Round(bh0 * s);
                    int x = bx0 + dx, y = by0 + dy;
                    if (x < 0 || y < 0 || x + w > frame.Width || y + h > frame.Height) continue;
                    var c = ImageOps.Crop(frame, x, y, w, h);
                    var t = ImageOps.Resize(baseTex!, w, h);
                    LocalNccDeviation.DeviationMap(
                        LocalNccDeviation.ToGrayFloat(c), LocalNccDeviation.ToGrayFloat(t),
                        w, h, win: 11, out var mean, addedOnly: false);
                    if (mean > bestMean) { bestMean = mean; bbx = x; bby = y; bbw = w; bbh = h; }
                }

        _out.WriteLine($"manual bbox ({bx0},{by0}) {bw0}x{bh0} → refined ({bbx},{bby}) {bbw}x{bbh}  meanNCC {0.736:0.000}→{bestMean:0.000}");

        var crop = ImageOps.Crop(frame, bbx, bby, bbw, bbh);
        var tex = ImageOps.Resize(baseTex!, bbw, bbh);
        var rect = new DetectionMapRect(0, 0, bbw, bbh, baseTex!.Width, baseTex.Height);
        var req = new DetectionRequest(crop, tex, rect, templates,
            RimMaskMode.DeviationFlood, LowNcc, TypeFloor, BlobOpts) { RenderSizePx = RenderSizePx };
        Dump("refined ALIGNED detect", detector.Detect(req));

        // Permissive gate so the inlier set (and the near-4th) is logged regardless.
        var engine = new MapCalibrationSolveEngine(detector,
            new CalibrationConfidenceGate(goodResidualThresholdPx: 1000, inlierFloor: 2),
            sp.GetRequiredService<ILoggerFactory>().CreateLogger("Engine"));
        DumpSolve("refined ALIGNED solve (permissive gate)", engine.Solve(req, refs));
    }

    /// <summary>
    /// The registration target: a meanNCC-maximizing coarse→fine search must
    /// reproduce the hand-verified pixel-perfect map bbox for each frame (the
    /// objective the current MapRectLocator does NOT optimize — it maximizes a
    /// downsampled global NCC, which lands ~10-60px / several % off, collapsing
    /// terrain cancellation). Aspect is locked to the texture's (h = w·H/W).
    /// </summary>
    [SkippableTheory]
    [InlineData("eltibule-frame1-rejected-3inliers.gray.png", 204, 133, 847, 841)]
    [InlineData("eltibule-frame2-accepted-7.61px.gray.png", 130, 60, 995, 986)]
    public void Registration_search_reproduces_ground_truth(string frameFile, int gtX, int gtY, int gtW, int gtH)
    {
        var framePath = Path.Combine(FrameDir, frameFile);
        Skip.IfNot(File.Exists(framePath), $"frame fixture missing: {framePath}");
        Skip.IfNot(File.Exists(Path.Combine(AssetCacheDir, $"map-texture-{Area}.bin")),
            $"base-texture cache missing under {AssetCacheDir}");
        using var sp = new ServiceCollection().AddMithrilMapCalibrationEngine(AssetCacheDir).BuildServiceProvider();
        var baseTex = sp.GetRequiredService<IBaseTextureProvider>().TryGetBaseTexture(Area);
        Skip.If(baseTex is null, "base texture failed to load");
        var frame = ImageIo.LoadGray(framePath);

        // Seed from the coarse auto-locator (the realistic starting point a fix would have).
        var seed = MapRectLocator.AutoDetect(frame, baseTex!, LowNcc, MapRectLocator.DefaultWorkingLongEdgePx);
        Skip.If(seed is null, "seed locate failed");
        _out.WriteLine($"{frameFile}: seed (auto) ({seed!.OriginX},{seed.OriginY}) {seed.Width}x{seed.Height} meanNCC={MeanNcc(frame, baseTex!, seed.OriginX, seed.OriginY, seed.Width):0.000}");

        // Coarse→fine→ultrafine local search maximizing meanNCC; h aspect-locked.
        // The meanNCC peak is razor-sharp (a few px off halves it), so the final
        // stage steps at 2px to actually land on it and cancel terrain.
        var (bx, by, bw, bm) = SearchRect(frame, baseTex!, seed.OriginX, seed.OriginY, seed.Width,
            offsets: new[] { -48, -32, -16, 0, 16, 32, 48 }, widthSteps: new[] { -120, -80, -40, 0, 40 });
        (bx, by, bw, bm) = SearchRect(frame, baseTex!, bx, by, bw,
            offsets: new[] { -12, -8, -4, 0, 4, 8, 12 }, widthSteps: new[] { -16, -8, 0, 8, 16 });
        (bx, by, bw, bm) = SearchRect(frame, baseTex!, bx, by, bw,
            offsets: new[] { -6, -4, -2, 0, 2, 4, 6 }, widthSteps: new[] { -6, -4, -2, 0, 2, 4, 6 });
        int bh = (int)Math.Round(bw * (double)baseTex!.Height / baseTex.Width);

        _out.WriteLine($"  recovered ({bx},{by}) {bw}x{bh} meanNCC={bm:0.000}   ground-truth ({gtX},{gtY}) {gtW}x{gtH}");
        _out.WriteLine($"  delta: origin=({bx - gtX},{by - gtY}) size=({bw - gtW},{bh - gtH})");

        // Solve at the recovered rect (aligned crop) to confirm the registration drives the good solve.
        var templates = sp.GetRequiredService<IIconTemplateProvider>().GetTemplates();
        var crop = ImageOps.Crop(frame, bx, by, bw, bh);
        var tex = ImageOps.Resize(baseTex!, bw, bh);
        var rect = new DetectionMapRect(0, 0, bw, bh, baseTex!.Width, baseTex.Height);
        var req = new DetectionRequest(crop, tex, rect, templates,
            RimMaskMode.DeviationFlood, LowNcc, TypeFloor, BlobOpts) { RenderSizePx = RenderSizePx };
        var solve = new MapCalibrationSolveEngine(
            sp.GetRequiredService<ICalibrationDetector>(), new CalibrationConfidenceGate()).Solve(req, EltibuleReferences());
        DumpSolve("  recovered-rect solve", solve);

        Math.Abs(bx - gtX).Should().BeLessThanOrEqualTo(12, "recovered origin.X must reproduce the ground-truth bbox");
        Math.Abs(by - gtY).Should().BeLessThanOrEqualTo(12, "recovered origin.Y must reproduce the ground-truth bbox");
        Math.Abs(bw - gtW).Should().BeLessThanOrEqualTo(12, "recovered width must reproduce the ground-truth bbox");
        bm.Should().BeGreaterThan(0.5, "the recovered rect must register (terrain cancels) — auto-locator gives <0.1");
    }

    private (int X, int Y, int W, double Mean) SearchRect(
        GrayImage frame, GrayImage baseTex, int cx, int cy, int cw, int[] offsets, int[] widthSteps)
    {
        double best = -1; int bx = cx, by = cy, bw = cw;
        foreach (var dw in widthSteps)
        {
            int w = cw + dw;
            int h = (int)Math.Round(w * (double)baseTex.Height / baseTex.Width);
            foreach (var dx in offsets)
                foreach (var dy in offsets)
                {
                    int x = cx + dx, y = cy + dy;
                    if (x < 0 || y < 0 || w <= 0 || h <= 0 || x + w > frame.Width || y + h > frame.Height) continue;
                    double m = MeanNcc(frame, baseTex, x, y, w);
                    if (m > best) { best = m; bx = x; by = y; bw = w; }
                }
        }
        return (bx, by, bw, best);
    }

    private static double MeanNcc(GrayImage frame, GrayImage baseTex, int x, int y, int w)
    {
        int h = (int)Math.Round(w * (double)baseTex.Height / baseTex.Width);
        if (x < 0 || y < 0 || x + w > frame.Width || y + h > frame.Height) return -1;
        var crop = ImageOps.Crop(frame, x, y, w, h);
        var tex = ImageOps.Resize(baseTex, w, h);
        LocalNccDeviation.DeviationMap(
            LocalNccDeviation.ToGrayFloat(crop), LocalNccDeviation.ToGrayFloat(tex),
            w, h, win: 11, out var mean, addedOnly: false);
        return mean;
    }

    /// <summary>
    /// Why frame 1 is NOT recoverable by accepting its 3-inlier fit — and why the
    /// inlier floor of 4 is load-bearing. Both frames are the SAME area, so a CORRECT
    /// world→texture calibration must be identical (a property of the area+texture).
    /// Frame 1's clean 3-inlier fit (1.42px residual) DIVERGES wildly from frame 2's
    /// trusted 10–12-inlier fit (≈40% scale, mirror flipped, ~200° rotation): three
    /// points under-constrain a 4-DOF similarity, so a low-residual fit can pin a
    /// confidently-WRONG transform. Lowering the floor / accepting 3-inlier solves
    /// would persist garbage calibrations — frame 1 needs a genuine 4th correct
    /// detection (better capture/detection), not a gate relaxation.
    /// </summary>
    [SkippableFact]
    public void Frame1_three_inlier_fit_is_untrustworthy()
    {
        Skip.IfNot(File.Exists(Path.Combine(AssetCacheDir, $"map-texture-{Area}.bin")),
            $"base-texture cache missing under {AssetCacheDir}");
        var f1 = Path.Combine(FrameDir, "eltibule-frame1-rejected-3inliers.gray.png");
        var f2 = Path.Combine(FrameDir, "eltibule-frame2-accepted-7.61px.gray.png");
        Skip.IfNot(File.Exists(f1) && File.Exists(f2), "frame fixtures missing");
        using var sp = new ServiceCollection().AddMithrilMapCalibrationEngine(AssetCacheDir).BuildServiceProvider();
        var baseTex = sp.GetRequiredService<IBaseTextureProvider>().TryGetBaseTexture(Area);
        Skip.If(baseTex is null, "base texture failed to load");
        var templates = sp.GetRequiredService<IIconTemplateProvider>().GetTemplates();
        var refs = EltibuleReferences();

        AreaCalibration Solve(string path, int bx, int by, int bw, int bh, int floor)
        {
            var crop = ImageOps.Crop(ImageIo.LoadGray(path), bx, by, bw, bh);
            var tex = ImageOps.Resize(baseTex!, bw, bh);
            var rect = new DetectionMapRect(0, 0, bw, bh, baseTex!.Width, baseTex.Height);
            var req = new DetectionRequest(crop, tex, rect, templates, RimMaskMode.DeviationFlood, LowNcc, TypeFloor, BlobOpts) { RenderSizePx = RenderSizePx };
            return new MapCalibrationSolveEngine(new DeviationBlobCalibrationDetector(),
                new CalibrationConfidenceGate(goodResidualThresholdPx: 1000, inlierFloor: floor)).Solve(req, refs).Calibration!;
        }

        var c1 = Solve(f1, 204, 133, 847, 841, floor: 2);   // frame1: its 3-inlier fit
        var c2 = Solve(f2, 130, 60, 995, 986, floor: 4);    // frame2: trusted 10-12-inlier fit
        Skip.If(c1 is null || c2 is null, "a solve returned null");

        string Fmt(AreaCalibration c) => $"scale={c.Scale:0.0000} rot={c.RotationRadians * 180 / Math.PI:0.00}° origin=({c.OriginX:0},{c.OriginY:0}) mirror={c.MirrorNorth} resid={c.ResidualPixels:0.00}";
        _out.WriteLine($"frame1 (3-inlier): {Fmt(c1)}");
        _out.WriteLine($"frame2 (trusted):  {Fmt(c2)}");
        double scaleErrPct = Math.Abs(c1.Scale - c2.Scale) / c2.Scale * 100;
        double rotErrDeg = Math.Abs(c1.RotationRadians - c2.RotationRadians) * 180 / Math.PI;
        double originDist = Math.Sqrt(Math.Pow(c1.OriginX - c2.OriginX, 2) + Math.Pow(c1.OriginY - c2.OriginY, 2));
        _out.WriteLine($"agreement: scaleΔ={scaleErrPct:0.0}%  rotΔ={rotErrDeg:0.00}°  originΔ={originDist:0.0}px  mirrorMatch={c1.MirrorNorth == c2.MirrorNorth}");

        // Verdict: frame1's 3-inlier fit does NOT match the trusted frame2 cal despite
        // its clean residual — proving a low-residual 3-inlier solve can be confidently
        // wrong. Accepting it (lowering the floor) would persist a garbage calibration;
        // the floor of 4 is load-bearing, and frame1 needs a real 4th detection.
        bool agrees = scaleErrPct < 3 && rotErrDeg < 2 && originDist < 25 && c1.MirrorNorth == c2.MirrorNorth;
        agrees.Should().BeFalse(
            "frame1's clean-residual 3-inlier fit is a WRONG transform vs the trusted frame2 cal — " +
            "demonstrating 3-inlier acceptance is unsafe and the inlier floor of 4 is load-bearing");
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
    internal static List<LandmarkReference> EltibuleReferences()
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
