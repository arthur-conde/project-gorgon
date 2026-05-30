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
                                            [--map-rect full | "left,top,width,height"]
                                            [--icon-render-size <px>] [--icon-size <name>=<W>x<H>]

            --icon-render-size (bootstrap): force the on-screen icon size (px) instead
              of the auto render-size sweep. Use if the sweep picks wrong (the run
              logs the per-size evidence + the chosen size).
            --icon-size (bootstrap): force ONE template to exact WxH, overriding the
              global render size — e.g. landmark_npc=24x26 when a sprite renders at an
              aspect the source art doesn't match (known: landmark_npc on Serbule).

            --map-rect (bootstrap only): skip NCC auto-locate of the map within the
              screenshot. The in-game map's restyling/fog/UI-frame can defeat the
              whole-texture NCC; this override is the documented fallback.
                full                 the screenshot IS the full-extent map texture
                                     (crop the gray UI frame off first).
                "left,top,width,height"  the map's pixel box within the screenshot.
              Applies to every area in the run, so use one area at a time when the
              box differs, or crop all screenshots and use `full`.
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
            double solvedScale = 0, predX = 0, ratioX = 0, insetMax = 0, simRms = 0;
            // H2 (affine-vs-similarity isotropy) has NO honest signal in measure
            // mode. The baseline doesn't persist the original click pixels the
            // similarity was fit on, so the only pixel side available is the
            // world points reprojected through that same solved similarity — an
            // affine fit to a pure-similarity cloud is ~0 by construction and
            // would read as a (false) "renderer is isotropic" result. Emit N/A
            // (NaN) here; the real affine-vs-similarity contest only exists in
            // `bootstrap` mode, where the pixel side is independently detected.
            var affRms = double.NaN;
            if (textureCal is not null && TryTextureSize(o["--textures"], area, out var tw, out var th))
            {
                var world = LoadWorldPoints(o["--landmarks"], o["--npcs"], area).Select(l => l.World).ToList();
                var m = InsetMetrics.Compute(textureCal, world, tw, th);
                solvedScale = textureCal.Scale; predX = m.PredictedScaleX;
                ratioX = m.ScaleRatioX; insetMax = m.InsetFracMax;
                simRms = textureCal.ResidualPixels;
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
        o.TryGetValue("--map-rect", out var mapRectOpt);
        var iconRenderSize = o.TryGetValue("--icon-render-size", out var irs) && int.TryParse(irs, out var irsv) ? irsv : 0;
        var iconSizeOverride = ParseIconSizeOverride(o);
        var rows = new List<StudyRecord>();

        foreach (var area in areas)
        {
            // One area's failure (missing input, map-rect miss, etc.) skips that
            // area and continues the run — a single bad screenshot must not abort
            // the whole study. Only UserFacingException is treated as a skip;
            // genuine bugs still propagate.
            try
            {
            var shotPath = Path.Combine(o["--screenshots"], area + ".png");
            if (!File.Exists(shotPath)) { Console.WriteLine($"[skip] no screenshot for {area}"); continue; }
            if (!TryTextureSize(o["--textures"], area, out var tw, out var th)) { Console.WriteLine($"[skip] no texture for {area}"); continue; }

            var world = LoadWorldPoints(o["--landmarks"], o["--npcs"], area).Select(l => l.World).ToList();
            var detected = DetectIcons(shotPath, o["--textures"], area, o["--icons"], icons, mapRectOpt, iconRenderSize, iconSizeOverride);
            if (detected.Count < 3) { Console.WriteLine($"[skip] <3 icons detected in {area}"); continue; }

            var result = ColdBootstrap.Run(world, detected, tw, th, axisThresholdPx: 8.0);
            if (result is null) { Console.WriteLine($"[skip] bootstrap returned null for {area}"); continue; }

            var orient = OrientationClass.Classify(result.Calibration.RotationRadians).NearestDeg;
            // `paired` must mean the recovered transform actually reproduces the
            // detection cloud — i.e. blind correspondence found GROUND TRUTH —
            // not merely that ≥3 icons were paired (a presence check that's true
            // even for a wrong-orientation run that fit a reflected subset). Tie
            // it to the global reprojection score: every world landmark must
            // reproject within detection-noise tolerance of a detected icon.
            var paired = result.CorrespondedCount >= 3 && result.GlobalReprojectionPx <= 8.0;
            rows.Add(new StudyRecord(area,
                result.Calibration.RotationRadians * 180.0 / Math.PI, orient,
                result.Calibration.MirrorNorth, result.Calibration.Scale, 0, 0, 0,
                // H2 is real here: affine fit over the same kept-inlier detected
                // pixels vs. that orientation's similarity residual (apples-to-apples).
                result.RefinedResidualPx, result.AffineResidualPx, result.CorrespondedCount, paired));
            }
            catch (UserFacingException ex)
            {
                Console.WriteLine($"[skip] {area}: {ex.Message}");
            }
        }

        Emit(o["--out"], "bootstrap", rows);
        return 0;
    }

    // ---- detection (texture-frame icon pixels) -----------------------------

    private static List<PixelPoint> DetectIcons(
        string screenshotPath, string texturesDir, string area, string iconsDir, IconIndex icons,
        string? mapRectOpt, int iconRenderSize, (string Name, int W, int H)? iconSizeOverride)
    {
        const double iconMinScore = 0.5;
        var screen = ImageIo.LoadGray(screenshotPath);
        var texturePath = MapTextureExtractor.EnsureExtractedOrCached(texturesDir, area)
            ?? throw new UserFacingException($"no cached texture PNG for {area} in {texturesDir}");
        var texture = ImageIo.LoadGray(texturePath);
        var rect = ResolveMapRect(mapRectOpt, screen, texture, screenshotPath);

        // PG ships the map-icon art at ~256 px in sharedassets0 but RENDERS every
        // icon at a single small on-screen size regardless of source dimensions
        // (#852). Matching the raw 256 px template never hits the ~20-40 px
        // rendered icons, so choose a render size first (override > sweep >
        // native) and scale templates to it (max-dim-based). Ported from the
        // proven tools/MapCalibrationFromScreenshot detector.
        var templates = new List<(IconMeta Meta, GrayImage Gray, GrayImage Alpha)>();
        foreach (var meta in icons.Icons)
        {
            var path = Path.Combine(iconsDir, meta.File);
            if (!File.Exists(path)) { Console.WriteLine($"  ! missing template {path}"); continue; }
            var (gray, alpha) = ImageIo.LoadGrayAndAlpha(path);
            templates.Add((meta, gray, alpha));
        }

        var maxDim = templates.Count == 0 ? 0 : templates.Max(t => Math.Max(t.Gray.Width, t.Gray.Height));
        int chosen;
        if (iconRenderSize > 0) { chosen = iconRenderSize; Console.WriteLine($"[scale] {area}: --icon-render-size {chosen}px"); }
        else if (maxDim > 64) chosen = SelectGlobalRenderSize(screen, templates, [12, 16, 20, 24, 30, 40, 56], iconMinScore);
        else chosen = 0; // templates already at render size

        var pixels = new List<PixelPoint>();
        foreach (var (meta, gray, alpha) in templates)
        {
            int rw, rh;
            if (iconSizeOverride is { } f && string.Equals(f.Name, meta.Name, StringComparison.Ordinal))
            {
                rw = f.W; rh = f.H;
            }
            else if (chosen == 0)
            {
                rw = gray.Width; rh = gray.Height;
            }
            else
            {
                var md = Math.Max(gray.Width, gray.Height);
                rw = Math.Max(1, gray.Width * chosen / md);
                rh = Math.Max(1, gray.Height * chosen / md);
            }

            var grayD = (rw == gray.Width && rh == gray.Height) ? gray : ImageIo.Resize(gray, rw, rh);
            var alphaD = (rw == alpha.Width && rh == alpha.Height) ? alpha : ImageIo.Resize(alpha, rw, rh);
            var hits = NccTemplateMatch.FindAll(screen, grayD, alphaD, iconMinScore, maxResults: 64);
            Console.WriteLine($"  [icon] {area}/{meta.Name} @ {rw}x{rh}: {hits.Count} hit(s)"
                              + (hits.Count > 0 ? $" (top {hits[0].Score:0.000})" : ""));
            foreach (var hit in hits)
            {
                var (cx, cy) = hit.Centre(rw, rh);
                // pivot-correct: anchor pixel = centre + (w*(pivot.x-0.5), h*(0.5-pivot.y))
                var ax = cx + rw * (meta.PivotX - 0.5);
                var ay = cy + rh * (0.5 - meta.PivotY);
                var (tx, ty) = rect.ScreenshotToTexture(ax, ay);
                pixels.Add(new PixelPoint(tx, ty));
            }
        }
        return pixels;
    }

    /// <summary>
    /// PG renders all map icons at one on-screen pixel size regardless of source
    /// artwork dimensions. Sweep a ladder of target render sizes, scale each
    /// template (max-dim) to the target, and pick the size maximising aggregate
    /// evidence (sum of each template's top NCC score above threshold). Ported
    /// from the proven screenshot calibrator's SelectGlobalRenderSize.
    /// </summary>
    private static int SelectGlobalRenderSize(
        GrayImage screenshot, List<(IconMeta Meta, GrayImage Gray, GrayImage Alpha)> templates,
        int[] candidates, double threshold)
    {
        var best = candidates[0];
        var bestEvidence = double.NegativeInfinity;
        foreach (var target in candidates)
        {
            double evidence = 0;
            var withHits = 0;
            foreach (var (_, gray, alpha) in templates)
            {
                var md = Math.Max(gray.Width, gray.Height);
                var rw = Math.Max(1, gray.Width * target / md);
                var rh = Math.Max(1, gray.Height * target / md);
                var top = NccTemplateMatch.FindBest(screenshot, ImageIo.Resize(gray, rw, rh), ImageIo.Resize(alpha, rw, rh), threshold);
                if (top is null) continue;
                evidence += top.Value.Score;
                withHits++;
            }
            Console.WriteLine($"[scale]   target={target}px  evidence={evidence:0.000}  hits={withHits}/{templates.Count}");
            if (evidence > bestEvidence) { bestEvidence = evidence; best = target; }
        }
        Console.WriteLine($"[scale] chose {best}px (evidence {bestEvidence:0.000})");
        return best;
    }

    private static (string Name, int W, int H)? ParseIconSizeOverride(Dictionary<string, string> o)
    {
        if (!o.TryGetValue("--icon-size", out var s)) return null;
        var eq = s.IndexOf('=');
        var dims = eq > 0 ? s[(eq + 1)..].Split('x', 'X') : [];
        if (eq <= 0 || dims.Length != 2 || !int.TryParse(dims[0], out var w) || !int.TryParse(dims[1], out var h) || w <= 0 || h <= 0)
        {
            throw new UserFacingException($"--icon-size must be name=WxH (positive ints), e.g. landmark_npc=24x26; got \"{s}\"");
        }
        return (s[..eq], w, h);
    }

    /// <summary>
    /// Resolves the screenshot→texture map rect. Default: NCC auto-locate of the
    /// full texture within the screenshot. The in-game map's restyling / fog /
    /// UI-frame can defeat that whole-texture correlation, so two overrides exist
    /// (the documented MapRectLocator fallback): <c>--map-rect full</c> treats the
    /// (frame-cropped) screenshot AS the full-extent texture; <c>--map-rect
    /// "l,t,w,h"</c> gives the map's pixel box within the screenshot explicitly.
    /// </summary>
    private static MapRect ResolveMapRect(string? opt, GrayImage screen, GrayImage texture, string screenshotPath)
    {
        if (opt is null)
        {
            return MapRectLocator.AutoDetect(screen, texture, minScore: 0.30)
                ?? throw new UserFacingException(
                    $"could not locate the map rect in {Path.GetFileName(screenshotPath)} — the in-game map's "
                    + "restyling/fog can defeat whole-texture NCC even on a fully-zoomed-out shot. "
                    + "Crop the gray UI frame off and pass --map-rect full, or pass --map-rect \"left,top,width,height\".");
        }

        if (string.Equals(opt, "full", StringComparison.OrdinalIgnoreCase))
        {
            return new MapRect(0, 0, screen.Width, screen.Height, texture.Width, texture.Height);
        }

        var parts = opt.Split(',', StringSplitOptions.TrimEntries);
        if (parts.Length != 4
            || !int.TryParse(parts[0], out var l) || !int.TryParse(parts[1], out var t)
            || !int.TryParse(parts[2], out var w) || !int.TryParse(parts[3], out var h)
            || w <= 0 || h <= 0
            || l < 0 || t < 0 || l + w > screen.Width || t + h > screen.Height)
        {
            throw new UserFacingException(
                $"--map-rect must be \"left,top,width,height\" (non-negative ints, positive w/h, inside the "
                + $"{screen.Width}x{screen.Height} screenshot) or \"full\"; got \"{opt}\"");
        }
        return new MapRect(l, t, w, h, texture.Width, texture.Height);
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
