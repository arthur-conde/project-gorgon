using System.Text.Json;
using Mithril.MapCalibration;
using Mithril.Tools.MapCalibration.Common;

namespace Mithril.Tools.MapCalibrationStudy;

internal static class Program
{
    private static int Main(string[] args)
    {
        try
        {
            if (args.Length == 0) return Usage();
            return args[0] switch
            {
                "measure" => Measure(ParseOptions(args)),
                "bootstrap" => Bootstrap(ParseOptions(args)),
                _ => Usage(),
            };
        }
        catch (UserFacingException ex)
        {
            Console.Error.WriteLine($"error: {ex.Message}");
            return 2;
        }
    }

    private static int Usage()
    {
        Console.Error.WriteLine("""
            usage:
              MapCalibrationStudy measure   --refinements <refinements.json> --baseline <baseline.json>
                                            --landmarks <landmarks.json> --npcs <npcs.json>
                                            --textures <dir> --areas <A,B,C> --out <dir>
              MapCalibrationStudy bootstrap --screenshots <dir> --textures <dir> --icons <dir>
                                            --landmarks <landmarks.json> --npcs <npcs.json>
                                            --areas <A,B,C> --out <dir>
            """);
        return 1;
    }

    // ---- measure (Half A) --------------------------------------------------

    private static int Measure(Dictionary<string, string> o)
    {
        var areas = o["--areas"].Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var source0 = LoadRefinements(o["--refinements"]);   // overlay-frame: rotation + handedness only
        var rows = new List<StudyRecord>();

        foreach (var area in areas)
        {
            // Rotation/handedness come from whichever solve we have (source 0
            // is enough for those, frame-invariant).
            source0.TryGetValue(area, out var overlayCal);
            var rotationDeg = overlayCal is null ? double.NaN : overlayCal.RotationRadians * 180.0 / Math.PI;
            var orient = overlayCal is null ? -1 : OrientationClass.Classify(overlayCal.RotationRadians).NearestDeg;
            var mirror = overlayCal?.MirrorNorth ?? false;

            // Scale/inset/affine need a TEXTURE-frame solve (the committed
            // baseline) + the texture dims + the world points.
            var textureCal = BaselineFile.TryReadAnchor(o["--baseline"], area);
            double solvedScale = 0, predX = 0, ratioX = 0, insetMax = 0, simRms = 0, affRms = 0;
            if (textureCal is not null && TryTextureSize(o["--textures"], area, out var tw, out var th))
            {
                var world = LoadWorldPoints(o["--landmarks"], o["--npcs"], area).Select(l => l.World).ToList();
                var m = InsetMetrics.Compute(textureCal, world, tw, th);
                solvedScale = textureCal.Scale; predX = m.PredictedScaleX;
                ratioX = m.ScaleRatioX; insetMax = m.InsetFracMax;
                simRms = textureCal.ResidualPixels;
                // affine RMS needs the (world↔pixel) pairs the baseline was fit
                // on; we don't persist those, so re-project the world points
                // through the solved transform as the pixel side (affine then
                // measures self-consistency of the model, the H2 question).
                var pts = world.Select(w => { var p = textureCal.WorldToWindow(w); return (w.X, w.Z, p.X, p.Y); }).ToList();
                affRms = pts.Count >= 3 ? AffineFit.Rms(pts) : 0;
            }

            rows.Add(new StudyRecord(area, rotationDeg, orient, mirror,
                solvedScale, predX, ratioX, insetMax, simRms, affRms, 0, false));
        }

        Emit(o["--out"], "measure", rows);
        return 0;
    }

    // ---- bootstrap (Half B) ------------------------------------------------

    private static int Bootstrap(Dictionary<string, string> o)
    {
        var areas = o["--areas"].Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var icons = IconTemplateExtractor.Load(o["--icons"]);
        var rows = new List<StudyRecord>();

        foreach (var area in areas)
        {
            var shotPath = Path.Combine(o["--screenshots"], area + ".png");
            if (!File.Exists(shotPath)) { Console.WriteLine($"[skip] no screenshot for {area}"); continue; }
            if (!TryTextureSize(o["--textures"], area, out var tw, out var th)) { Console.WriteLine($"[skip] no texture for {area}"); continue; }

            var world = LoadWorldPoints(o["--landmarks"], o["--npcs"], area).Select(l => l.World).ToList();
            var detected = DetectIcons(shotPath, o["--textures"], area, o["--icons"], icons);
            if (detected.Count < 3) { Console.WriteLine($"[skip] <3 icons detected in {area}"); continue; }

            var result = ColdBootstrap.Run(world, detected, tw, th, axisThresholdPx: 8.0);
            if (result is null) { Console.WriteLine($"[skip] bootstrap returned null for {area}"); continue; }

            var orient = OrientationClass.Classify(result.Calibration.RotationRadians).NearestDeg;
            rows.Add(new StudyRecord(area,
                result.Calibration.RotationRadians * 180.0 / Math.PI, orient,
                result.Calibration.MirrorNorth, result.Calibration.Scale, 0, 0, 0,
                result.RefinedResidualPx, 0, result.CorrespondedCount, result.CorrespondedCount >= 3));
        }

        Emit(o["--out"], "bootstrap", rows);
        return 0;
    }

    // ---- detection (texture-frame icon pixels) -----------------------------

    private static List<PixelPoint> DetectIcons(
        string screenshotPath, string texturesDir, string area, string iconsDir, IconIndex icons)
    {
        var screen = ImageIo.LoadGray(screenshotPath);
        var texturePath = MapTextureExtractor.EnsureExtractedOrCached(texturesDir, area)
            ?? throw new UserFacingException($"no cached texture PNG for {area} in {texturesDir}");
        var texture = ImageIo.LoadGray(texturePath);
        var rect = MapRectLocator.AutoDetect(screen, texture, minScore: 0.30)
            ?? throw new UserFacingException($"could not locate the map rect in {Path.GetFileName(screenshotPath)} — is it zoomed fully out?");

        var pixels = new List<PixelPoint>();
        foreach (var meta in icons.Icons)
        {
            var (gray, alpha) = ImageIo.LoadGrayAndAlpha(Path.Combine(iconsDir, meta.File));
            var hits = NccTemplateMatch.FindAll(screen, gray, alpha, minScore: 0.5, maxResults: 64);
            foreach (var hit in hits)
            {
                var (cx, cy) = hit.Centre(meta.Width, meta.Height);
                // pivot-correct: anchor pixel = centre + (w*(pivot.x-0.5), h*(0.5-pivot.y))
                var ax = cx + meta.Width * (meta.PivotX - 0.5);
                var ay = cy + meta.Height * (0.5 - meta.PivotY);
                var (tx, ty) = rect.ScreenshotToTexture(ax, ay);
                pixels.Add(new PixelPoint(tx, ty));
            }
        }
        return pixels;
    }

    // ---- helpers -----------------------------------------------------------

    private static Dictionary<string, AreaCalibration> LoadRefinements(string path)
    {
        using var doc = JsonDocument.Parse(File.ReadAllText(path));
        var map = new Dictionary<string, AreaCalibration>(StringComparer.Ordinal);
        if (!doc.RootElement.TryGetProperty("calibrations", out var cals)) return map;
        foreach (var p in cals.EnumerateObject())
        {
            var v = p.Value;
            double D(string k) => v.TryGetProperty(k, out var e) ? e.GetDouble() : 0;
            bool B(string k) => v.TryGetProperty(k, out var e) && e.GetBoolean();
            int I(string k) => v.TryGetProperty(k, out var e) ? e.GetInt32() : 0;
            map[p.Name] = new AreaCalibration(D("scale"), D("rotationRadians"), D("originX"), D("originY"),
                I("referenceCount"), D("residualPixels")) { MirrorNorth = B("mirrorNorth") };
        }
        return map;
    }

    private static List<LandmarkRef> LoadWorldPoints(string landmarksPath, string npcsPath, string area)
    {
        var list = new List<LandmarkRef>(LandmarksReader.LoadForArea(landmarksPath, area));
        list.AddRange(NpcsReader.LoadForArea(npcsPath, area));
        return list;
    }

    private static bool TryTextureSize(string texturesDir, string area, out int w, out int h)
    {
        w = h = 0;
        var path = MapTextureExtractor.EnsureExtractedOrCached(texturesDir, area);
        if (path is null || !File.Exists(path)) return false;
        var (_, iw, ih) = ImageIo.LoadBgra(path);
        w = iw; h = ih;
        return true;
    }

    private static void Emit(string outDir, string mode, IReadOnlyList<StudyRecord> rows)
    {
        Directory.CreateDirectory(outDir);
        File.WriteAllText(Path.Combine(outDir, $"{mode}.csv"), StudyRecord.ToCsv(rows));
        File.WriteAllText(Path.Combine(outDir, $"{mode}.md"), StudyRecord.ToMarkdown(rows));
        Console.WriteLine(StudyRecord.ToMarkdown(rows));
        Console.WriteLine($"[{mode}] wrote {rows.Count} rows to {outDir}");
    }

    private static Dictionary<string, string> ParseOptions(string[] args)
    {
        var o = new Dictionary<string, string>(StringComparer.Ordinal);
        for (var i = 1; i < args.Length - 1; i++)
            if (args[i].StartsWith("--", StringComparison.Ordinal)) o[args[i]] = args[i + 1];
        return o;
    }
}
