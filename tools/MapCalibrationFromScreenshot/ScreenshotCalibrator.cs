using Mithril.MapCalibration;

namespace Mithril.Tools.MapCalibrationFromScreenshot;

/// <summary>
/// Coordinates the four image-processing phases that turn a screenshot into a
/// solved <see cref="AreaCalibration"/>:
/// <list type="number">
///   <item>locate the map rect inside the screenshot,</item>
///   <item>detect each landmark icon template via NCC,</item>
///   <item>assign detections to <c>landmarks.json</c> entries with a Type filter +
///         brute-force-permutation residual minimisation (cheap RANSAC for the
///         small fan-out the issue cares about),</item>
///   <item>feed the (world, texture-pixel) pairs into
///         <see cref="LandmarkCalibrationSolver.Solve"/>.</item>
/// </list>
///
/// <para>Pivot correction is applied per detection: PG's pin-shaped icons are
/// authored with pivot ≈ (0.5, 0) so the world-anchor pixel is the bottom tip,
/// not the icon centre. Without this the residual systematically misses the
/// 12 px threshold (issue #852 comment).</para>
/// </summary>
internal static class ScreenshotCalibrator
{
    // Default NCC threshold for accepting a detection. NCC peaks at 1.0.
    // 0.5 is a reasonable default — clean PG icon matches typically score
    // 0.5–0.9; the synthetic self-test relies on this floor to suppress
    // cross-shape false positives. Real screenshots may need tuning: PG's
    // rendering effects (shading, color tint, anti-aliasing) push some valid
    // matches into the 0.3–0.45 band. Override via --detection-threshold
    // when a real area has low recall, then verify the assignment is sane
    // before committing the result.

    public static CalibrationResult Calibrate(CalibrationInputs inputs)
    {
        var screenshotGray = ImageIo.LoadGray(inputs.ScreenshotPath);
        var textureGray = ImageIo.LoadGray(inputs.AreaMapPath);
        Console.WriteLine($"[screenshot] {screenshotGray.Width}x{screenshotGray.Height} / texture {textureGray.Width}x{textureGray.Height}");

        byte[]? debugBgra = null;
        int debugW = 0, debugH = 0;
        if (inputs.DebugImagePath is not null)
        {
            (debugBgra, debugW, debugH) = ImageIo.LoadBgra(inputs.ScreenshotPath);
        }

        var iconIndex = IconTemplateExtractor.Load(inputs.IconsDir);
        var landmarks = LandmarksReader.LoadForArea(inputs.LandmarksJsonPath, inputs.Area);
        var npcs = NpcsReader.LoadForArea(inputs.NpcsJsonPath, inputs.Area);
        var allRefs = landmarks.Concat(npcs).ToList();
        Console.WriteLine($"[refs] {landmarks.Count} landmarks + {npcs.Count} NPCs for {inputs.Area} from {Path.GetFileName(inputs.LandmarksJsonPath)} / {Path.GetFileName(inputs.NpcsJsonPath)}");

        // Phase: locate the map rect inside the screenshot.
        MapRect mapRect;
        if (inputs.MapRectOverride is { } o)
        {
            mapRect = new MapRect(o.X, o.Y, o.W, o.H, textureGray.Width, textureGray.Height);
            Console.WriteLine($"[locate] using --map-rect override: ({mapRect.OriginX},{mapRect.OriginY}) size {mapRect.Width}x{mapRect.Height}");
        }
        else
        {
            // Coarse pass at half-resolution to keep NCC sub-second per scale, then
            // scale the result back. Fractional downsample factors on the texture
            // side cover the "map fills the whole screenshot" case that integer
            // factors miss badly.
            var screenshotDown = ImageIo.Downsample(screenshotGray, 2);
            var textureDown = ImageIo.Downsample(textureGray, 2);
            Console.WriteLine($"[locate] coarse NCC scale-ladder (screenshot {screenshotDown.Width}x{screenshotDown.Height}, texture {textureDown.Width}x{textureDown.Height})");
            var rectDown = MapRectLocator.AutoDetect(screenshotDown, textureDown, minScore: 0.4);
            if (rectDown is null)
            {
                return new CalibrationResult(
                    Calibration: null,
                    AssignedReferences: [],
                    FailureReason: "map rect auto-detect found no match >= 0.4. Pass --map-rect <x,y,w,h> to skip auto-detect (read the bbox of the visible map area off the screenshot in any image viewer).");
            }
            mapRect = new MapRect(
                OriginX: rectDown.OriginX * 2,
                OriginY: rectDown.OriginY * 2,
                Width: rectDown.Width * 2,
                Height: rectDown.Height * 2,
                TextureWidth: textureGray.Width,
                TextureHeight: textureGray.Height,
                AutoDetectScore: rectDown.AutoDetectScore,
                SourceScaleFactor: rectDown.SourceScaleFactor);
            Console.WriteLine($"[locate] map at screenshot ({mapRect.OriginX},{mapRect.OriginY}) size {mapRect.Width}x{mapRect.Height} (score={mapRect.AutoDetectScore:0.000}, downsample={mapRect.SourceScaleFactor:0.00})");
        }

        // Phase: detect every landmark icon variant and pair with landmarks.json.
        var assigned = new List<AssignedReference>();
        var (detectionsByType, rawDetections) = DetectIconsByType(screenshotGray, inputs.IconsDir, iconIndex, inputs.DetectionThreshold);

        if (debugBgra is not null)
        {
            // Mark every NCC detection that cleared threshold. Cyan rect = match
            // bbox (at the multi-scale render size), red cross = pivot-corrected
            // anchor (the pixel fed to solver).
            foreach (var (icon, det, rw, rh) in rawDetections)
            {
                ImageIo.DrawRect(debugBgra, debugW, debugH, det.X, det.Y, rw, rh, 0, 255, 255);
                var (cx, cy) = det.Centre(rw, rh);
                int ax = (int)Math.Round(cx + rw * (icon.PivotX - 0.5));
                int ay = (int)Math.Round(cy + rh * (0.5 - icon.PivotY));
                ImageIo.DrawCross(debugBgra, debugW, debugH, ax, ay, 4, 255, 0, 0);
            }
        }

        foreach (var typeGroup in detectionsByType)
        {
            if (typeGroup.Key == "Player") continue; // handled below

            var dets = typeGroup.Value;
            var areaRefs = allRefs.Where(l => string.Equals(l.Type, typeGroup.Key, StringComparison.Ordinal)).ToList();
            if (areaRefs.Count == 0)
            {
                Console.WriteLine($"[detect] {typeGroup.Key}: {dets.Count} detections — no landmark of this type in {inputs.Area}; skipping");
                continue;
            }
            Console.WriteLine($"[detect] {typeGroup.Key}: {dets.Count} detections vs {areaRefs.Count} landmarks in area");

            // Assignment: when same count, pair lexicographically (the solver
            // tolerates this — handedness selection absorbs any rotation). When
            // n_detected != n_in_area, try brute-force subsets, keep lowest-residual.
            var typeAssigned = AssignAndScore(dets, areaRefs, mapRect, typeGroup.Key);
            assigned.AddRange(typeAssigned);
        }

        if (inputs.PlayerCoord is { } pc && detectionsByType.TryGetValue("Player", out var playerDets))
        {
            // Player pin: best variant wins. Pair its world-anchor pixel with the
            // user-supplied player coord (or coord read from the log).
            var best = playerDets.OrderByDescending(d => d.MatchScore).FirstOrDefault();
            if (best is not null)
            {
                var (tx, ty) = mapRect.ScreenshotToTexture(best.AnchorScreenshotX, best.AnchorScreenshotY);
                assigned.Add(new AssignedReference(
                    Label: $"Player ({best.IconName})",
                    WorldX: pc.X,
                    WorldZ: pc.Z,
                    PixelX: tx,
                    PixelY: ty,
                    MatchScore: best.MatchScore));
                Console.WriteLine($"[detect] Player: best variant '{best.IconName}' score={best.MatchScore:0.000}");
            }
            else
            {
                Console.WriteLine("[detect] Player: no variant scored >= threshold; --player-coord ignored");
            }
        }

        if (assigned.Count < 2)
        {
            return new CalibrationResult(
                Calibration: null,
                AssignedReferences: assigned,
                FailureReason: $"only {assigned.Count} reference(s) usable; solver needs >= 2. Detected {detectionsByType.Sum(kv => kv.Value.Count)} icons total but assignment culled to {assigned.Count}.");
        }

        // Phase: write debug image if requested (regardless of solve success).
        if (debugBgra is not null && inputs.DebugImagePath is not null)
        {
            // Map-rect outline in green so the user can see what the locator picked.
            ImageIo.DrawRect(debugBgra, debugW, debugH, mapRect.OriginX, mapRect.OriginY,
                mapRect.Width, mapRect.Height, 0, 255, 0);
            ImageIo.SaveBgraPng(debugBgra, debugW, debugH, inputs.DebugImagePath);
            Console.WriteLine($"[debug] annotated screenshot -> {inputs.DebugImagePath}");
        }

        // Phase: solve.
        var refs = assigned
            .Select(a => new LandmarkCalibrationSolver.Reference(a.WorldX, a.WorldZ, new PixelPoint(a.PixelX, a.PixelY)))
            .ToList();
        var cal = LandmarkCalibrationSolver.Solve(refs);
        if (cal is null)
        {
            return new CalibrationResult(null, assigned, "solver returned null (degenerate references — all collinear?)");
        }

        // Persist CalibrationZoom = 1.0 because we've already rescaled to
        // texture coords via mapRect. Source = UserRefinement (issue #852 says
        // these populate the bundled baseline; rewriter sets source on write).
        cal = cal with { CalibrationZoom = inputs.Zoom, Source = CalibrationSource.BundledBaseline };
        return new CalibrationResult(cal, assigned, FailureReason: null);
    }

    private static (Dictionary<string, List<TypedDetection>> ByType, List<(IconMeta Icon, Detection Det, int RenderW, int RenderH)> Raw)
        DetectIconsByType(GrayImage screenshot, string iconsDir, IconIndex iconIndex, double threshold)
    {
        // Load every template once, gray + alpha pair.
        var templates = new List<(IconMeta Icon, GrayImage Gray, GrayImage Alpha)>();
        foreach (var icon in iconIndex.Icons)
        {
            var iconPath = Path.Combine(iconsDir, icon.File);
            if (!File.Exists(iconPath))
            {
                Console.WriteLine($"  ! template file missing: {iconPath} (skipping)");
                continue;
            }
            var (gray, alpha) = ImageIo.LoadGrayAndAlpha(iconPath);
            templates.Add((icon, gray, alpha));
        }

        // Pick the global render size: PG renders all map icons at a single
        // consistent on-screen pixel size regardless of source artwork
        // dimensions (verified 2026-05-29 user observation). Treating each
        // icon's best scale independently invites cross-scale inconsistency;
        // a single shared render size gives consistent geometry to the solver.
        //
        // For large templates (artwork at sharedassets0's ~256 px), sweep a
        // ladder of target on-screen sizes and pick the one that maximises
        // aggregate evidence (sum of top scores across all icons). For small
        // templates already at render size (e.g. self-test's 24-32 px synth),
        // skip the sweep and use native — there's nothing to choose.
        int maxTemplateDim = templates.Count == 0 ? 0 : templates.Max(t => Math.Max(t.Gray.Width, t.Gray.Height));
        bool needsScaleSearch = maxTemplateDim > 64;
        int chosenSize = needsScaleSearch
            ? SelectGlobalRenderSize(screenshot, templates, [12, 16, 20, 24, 30, 40, 56], threshold)
            : 0;  // 0 = use each template's native size
        if (!needsScaleSearch)
        {
            Console.WriteLine("[scale] templates already at render size; using native per-icon dimensions");
        }

        // Final detection pass — at the chosen render size if a scale was
        // selected, otherwise each template at its native dimensions (no
        // resize), which matters when icons have heterogeneous shapes like
        // the synthetic self-test.
        var byType = new Dictionary<string, List<TypedDetection>>(StringComparer.Ordinal);
        var raw = new List<(IconMeta, Detection, int, int)>();
        foreach (var (icon, gray, alpha) in templates)
        {
            int rw, rh;
            if (chosenSize == 0)
            {
                rw = gray.Width; rh = gray.Height;
            }
            else
            {
                int aspect = Math.Max(gray.Width, gray.Height);
                rw = Math.Max(1, gray.Width * chosenSize / aspect);
                rh = Math.Max(1, gray.Height * chosenSize / aspect);
            }
            var grayD = (rw == gray.Width && rh == gray.Height) ? gray : ImageIo.Resize(gray, rw, rh);
            var alphaD = (rw == alpha.Width && rh == alpha.Height) ? alpha : ImageIo.Resize(alpha, rw, rh);
            var hits = NccTemplateMatch.FindAll(screenshot, grayD, alphaD, threshold, maxResults: 64);
            Console.WriteLine($"  [icon] {icon.Name} @ {rw}x{rh}: {hits.Count} detections >= {threshold:0.00}" +
                              (hits.Count > 0 ? $" (top {hits[0].Score:0.000})" : ""));
            if (hits.Count == 0) continue;

            if (!byType.TryGetValue(icon.LandmarkType, out var list))
            {
                list = new List<TypedDetection>();
                byType[icon.LandmarkType] = list;
            }
            foreach (var h in hits)
            {
                raw.Add((icon, h, rw, rh));
                var (cx, cy) = h.Centre(rw, rh);
                var anchorX = cx + rw * (icon.PivotX - 0.5);
                var anchorY = cy + rh * (0.5 - icon.PivotY);
                list.Add(new TypedDetection(
                    IconName: icon.Name,
                    AnchorScreenshotX: anchorX,
                    AnchorScreenshotY: anchorY,
                    MatchScore: h.Score));
            }
        }
        return (byType, raw);
    }

    private static int SelectGlobalRenderSize(
        GrayImage screenshot,
        List<(IconMeta Icon, GrayImage Gray, GrayImage Alpha)> templates,
        int[] candidates,
        double threshold)
    {
        if (candidates.Length == 1)
        {
            Console.WriteLine($"[scale] single candidate {candidates[0]} px (templates already at render size)");
            return candidates[0];
        }

        int best = candidates[0];
        double bestEvidence = double.NegativeInfinity;
        Console.WriteLine($"[scale] sweeping {candidates.Length} render-size candidates across {templates.Count} templates");
        foreach (var target in candidates)
        {
            // Aggregate evidence at this candidate: sum of top score per icon
            // (above threshold). Captures "this scale gives at least one good
            // match per icon" without letting one super-matched icon dominate.
            double evidence = 0;
            int templatesWithHits = 0;
            foreach (var (_, gray, alpha) in templates)
            {
                int aspect = Math.Max(gray.Width, gray.Height);
                int rw = Math.Max(1, gray.Width * target / aspect);
                int rh = Math.Max(1, gray.Height * target / aspect);
                var grayD = ImageIo.Resize(gray, rw, rh);
                var alphaD = ImageIo.Resize(alpha, rw, rh);
                var top = NccTemplateMatch.FindBest(screenshot, grayD, alphaD, threshold);
                if (top is null) continue;
                evidence += top.Value.Score;
                templatesWithHits++;
            }
            Console.WriteLine($"[scale]   target={target}  evidence={evidence:0.000}  templatesWithHits={templatesWithHits}/{templates.Count}");
            if (evidence > bestEvidence)
            {
                bestEvidence = evidence;
                best = target;
            }
        }
        Console.WriteLine($"[scale] chose render size {best} px (aggregate evidence {bestEvidence:0.000})");
        return best;
    }

    private static IReadOnlyList<AssignedReference> AssignAndScore(
        List<TypedDetection> detections, List<LandmarkRef> areaRefs, MapRect mapRect, string typeName)
    {
        // v1 assignment policy:
        //   - dedup detections that pivot-corrected onto the same pixel,
        //   - trim to areaRefs.Count by best score (icon shape can score on noise),
        //   - pair sequentially with areaRefs.
        //
        // For single-landmark types (areaRefs.Count == 1) this is exact: the
        // best detection IS the landmark. For multi-landmark types the order
        // is the file order of landmarks.json, which is arbitrary — the solver's
        // handedness step absorbs simple swaps, but a real cluster of N
        // same-type landmarks in tight visual proximity may misassign. Issue
        // #852's "Same-type clustering" verification-owed item; a future
        // upgrade can swap this for a global-RANSAC sweep that uses every
        // type's detections jointly.

        var unique = new List<TypedDetection>();
        foreach (var d in detections.OrderByDescending(d => d.MatchScore))
        {
            if (unique.Any(u => Math.Abs(u.AnchorScreenshotX - d.AnchorScreenshotX) < 4 &&
                                Math.Abs(u.AnchorScreenshotY - d.AnchorScreenshotY) < 4))
                continue;
            unique.Add(d);
        }
        detections = unique;

        if (detections.Count > areaRefs.Count)
        {
            detections = [.. detections.OrderByDescending(d => d.MatchScore).Take(areaRefs.Count)];
        }

        var result = new List<AssignedReference>(detections.Count);
        for (int i = 0; i < detections.Count; i++)
        {
            var (tx, ty) = mapRect.ScreenshotToTexture(detections[i].AnchorScreenshotX, detections[i].AnchorScreenshotY);
            result.Add(new AssignedReference(
                Label: $"{typeName}:{areaRefs[i].Name} ({detections[i].IconName})",
                WorldX: areaRefs[i].World.X,
                WorldZ: areaRefs[i].World.Z,
                PixelX: tx,
                PixelY: ty,
                MatchScore: detections[i].MatchScore));
        }
        return result;
    }

    private sealed record TypedDetection(string IconName, double AnchorScreenshotX, double AnchorScreenshotY, double MatchScore);
}
