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
        var (detectionsByType, rawDetections) = DetectIconsByType(screenshotGray, inputs.IconsDir, iconIndex, inputs.DetectionThreshold, inputs.IconRenderSizeOverride);

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

        // Per-type summary (informational; assignment happens via RANSAC below
        // over the full pool).
        foreach (var typeGroup in detectionsByType)
        {
            var areaRefs = allRefs.Where(l => string.Equals(l.Type, typeGroup.Key, StringComparison.Ordinal)).ToList();
            Console.WriteLine($"[detect] {typeGroup.Key}: {typeGroup.Value.Count} detections vs {areaRefs.Count} landmarks in area");
        }

        // Phase: assignment via RANSAC over the full (detection, ref) pool.
        // Sequential pairing per-type produced geometrically-incoherent results
        // (sub-meter NPCs paired by file order, not by actual map position).
        // RANSAC picks 2 random same-type pairs, solves a candidate calibration,
        // counts how many other detections project within threshold of a
        // same-type ref. Best inlier count wins.
        var assigned = RansacAssign(detectionsByType, allRefs, mapRect);
        Console.WriteLine($"[ransac] {assigned.Count} inlier references kept");

        if (assigned.Count < 2)
        {
            return new CalibrationResult(
                Calibration: null,
                AssignedReferences: assigned,
                FailureReason: $"RANSAC could not find a calibration with >= 2 geometrically-consistent (detection, ref) pairs. Total detections: {detectionsByType.Sum(kv => kv.Value.Count)}. Inspect --debug-image to see if detections are clustered on actual icons.");
        }

        // Final solve over the inlier set (refines the 2-point RANSAC seed).
        var refs = assigned
            .Select(a => new LandmarkCalibrationSolver.Reference(a.WorldX, a.WorldZ, new PixelPoint(a.PixelX, a.PixelY)))
            .ToList();
        var cal = LandmarkCalibrationSolver.Solve(refs);
        if (cal is null)
        {
            return new CalibrationResult(null, assigned, "solver returned null on RANSAC inliers (degenerate — all collinear?)");
        }
        cal = cal with { CalibrationZoom = inputs.Zoom, Source = CalibrationSource.BundledBaseline };

        // Phase: write debug image (deferred so we can color-code RANSAC inliers).
        if (debugBgra is not null && inputs.DebugImagePath is not null)
        {
            // Overlay green rects + crosses for inliers RANSAC actually kept,
            // on top of the cyan/red raw-detection layer. Converts inlier
            // texture-pixel coords back to screenshot pixels for drawing.
            var scaleX = (double)mapRect.Width / mapRect.TextureWidth;
            var scaleY = (double)mapRect.Height / mapRect.TextureHeight;
            foreach (var a in assigned)
            {
                int sx = (int)Math.Round(a.PixelX * scaleX + mapRect.OriginX);
                int sy = (int)Math.Round(a.PixelY * scaleY + mapRect.OriginY);
                // Slightly oversized green rect to stand out over the cyan raw rect.
                ImageIo.DrawRect(debugBgra, debugW, debugH, sx - 12, sy - 12, 24, 24, 0, 255, 0);
                ImageIo.DrawRect(debugBgra, debugW, debugH, sx - 13, sy - 13, 26, 26, 0, 255, 0);
                ImageIo.DrawCross(debugBgra, debugW, debugH, sx, sy, 6, 0, 255, 0);
            }
            ImageIo.DrawRect(debugBgra, debugW, debugH, mapRect.OriginX, mapRect.OriginY,
                mapRect.Width, mapRect.Height, 0, 255, 0);
            ImageIo.SaveBgraPng(debugBgra, debugW, debugH, inputs.DebugImagePath);
            Console.WriteLine($"[debug] annotated screenshot -> {inputs.DebugImagePath}");
            Console.WriteLine("[debug]   cyan rect / red cross = every NCC detection that cleared threshold");
            Console.WriteLine("[debug]   green rect / green cross = the RANSAC inliers actually used in the solve");
            Console.WriteLine("[debug]   green outline = the map rect");
        }

        return new CalibrationResult(cal, assigned, FailureReason: null);
    }

    // Inlier threshold for RANSAC: a detection is an inlier of a candidate
    // calibration if its pivot-corrected pixel is within this many texture
    // pixels of where the calibration projects a same-type ref. Tightened
    // from 50 → 15 after first real-screenshot run (Serbule): the looser
    // threshold let noisy NPC detections in the central-town cluster pair
    // with NPCs they weren't actually positioned at, dragging a 3-perfect-
    // landmark fit into a wrong-but-self-consistent 11-inlier fit. 15 px in
    // texture space is ~7 px on a typical screenshot — tighter than icon
    // size, so a detection must be on a real icon to qualify.
    private const double RansacInlierPx = 15.0;

    // Random-sample iterations. 800 handles ~80% outliers at 95% confidence
    // for a 2-point seed; cheap because each iteration is just a 2-point
    // solver invocation + a linear inlier scan over the pool.
    private const int RansacIterations = 800;

    private static IReadOnlyList<AssignedReference> RansacAssign(
        Dictionary<string, List<TypedDetection>> detectionsByType,
        List<LandmarkRef> allRefs,
        MapRect mapRect)
    {
        // Build pool: (texture-pixel detection, candidate refs of same type).
        // Work in texture-pixel space so the inlier predicate is in a stable
        // coord system independent of the screenshot's pan/zoom.
        var pool = new List<(TypedDetection Det, double Tx, double Ty, IReadOnlyList<LandmarkRef> Candidates)>();
        foreach (var kv in detectionsByType)
        {
            var typeRefs = allRefs.Where(r => string.Equals(r.Type, kv.Key, StringComparison.Ordinal)).ToList();
            if (typeRefs.Count == 0) continue;
            foreach (var det in kv.Value)
            {
                var (tx, ty) = mapRect.ScreenshotToTexture(det.AnchorScreenshotX, det.AnchorScreenshotY);
                pool.Add((det, tx, ty, typeRefs));
            }
        }
        if (pool.Count < 2) return [];

        var rng = new Random(852);  // deterministic seed for reproducible runs
        int bestInlierCount = 0;
        double bestResidual = double.PositiveInfinity;
        List<AssignedReference> bestAssigned = [];

        for (int iter = 0; iter < RansacIterations; iter++)
        {
            int i1 = rng.Next(pool.Count);
            int i2 = rng.Next(pool.Count);
            if (i1 == i2) continue;
            var e1 = pool[i1];
            var e2 = pool[i2];
            if (Math.Abs(e1.Tx - e2.Tx) < 5 && Math.Abs(e1.Ty - e2.Ty) < 5) continue;

            var r1 = e1.Candidates[rng.Next(e1.Candidates.Count)];
            var r2 = e2.Candidates[rng.Next(e2.Candidates.Count)];
            if (r1.World.X == r2.World.X && r1.World.Z == r2.World.Z) continue;

            var seed = LandmarkCalibrationSolver.Solve([
                new LandmarkCalibrationSolver.Reference(r1.World.X, r1.World.Z, new PixelPoint(e1.Tx, e1.Ty)),
                new LandmarkCalibrationSolver.Reference(r2.World.X, r2.World.Z, new PixelPoint(e2.Tx, e2.Ty)),
            ]);
            if (seed is null) continue;

            // For each pool entry, project each of its candidate refs through
            // the seed; the closest projection wins. If within RansacInlierPx
            // of the detected pixel, it's a candidate inlier.
            //
            // Two-stage de-dup: (a) per detection, keep the single best ref
            // (already in the inner loop). (b) per ref, keep the single best
            // detection — otherwise multiple noisy detections all "claim" the
            // same real landmark, inflating the inlier count and dragging the
            // final solve's residual upward.
            var perDetCandidates = new List<(int PoolIdx, LandmarkRef Ref, double Dist)>();
            for (int pi = 0; pi < pool.Count; pi++)
            {
                var e = pool[pi];
                LandmarkRef? best = null;
                double bestDist = double.PositiveInfinity;
                foreach (var cand in e.Candidates)
                {
                    var pred = seed.WorldToWindow(cand.World);
                    var dx = pred.X - e.Tx;
                    var dy = pred.Y - e.Ty;
                    var d = Math.Sqrt(dx * dx + dy * dy);
                    if (d < bestDist) { bestDist = d; best = cand; }
                }
                if (best is not null && bestDist <= RansacInlierPx)
                {
                    perDetCandidates.Add((pi, best, bestDist));
                }
            }

            // Per ref, keep the detection with the smallest distance.
            var bestPerRef = new Dictionary<(double, double), (int PoolIdx, LandmarkRef Ref, double Dist)>();
            foreach (var cand in perDetCandidates)
            {
                var key = (cand.Ref.World.X, cand.Ref.World.Z);
                if (!bestPerRef.TryGetValue(key, out var existing) || cand.Dist < existing.Dist)
                {
                    bestPerRef[key] = cand;
                }
            }

            var inliers = new List<AssignedReference>(bestPerRef.Count);
            foreach (var cand in bestPerRef.Values)
            {
                var e = pool[cand.PoolIdx];
                inliers.Add(new AssignedReference(
                    Label: $"{e.Det.IconName}:{cand.Ref.Name}",
                    WorldX: cand.Ref.World.X,
                    WorldZ: cand.Ref.World.Z,
                    PixelX: e.Tx,
                    PixelY: e.Ty,
                    MatchScore: e.Det.MatchScore));
            }
            // Reject geometrically-degenerate inlier sets: if every inlier is
            // at the SAME small region of the texture (e.g. an edge-artifact
            // cluster where NCC false-positives at noisy small scales),
            // any pairing through them yields a trivial-but-meaningless fit
            // (tiny scale collapses world coords to a point). Require the
            // bounding box of detected pixels to span at least 100 px in the
            // larger dim — a real area calibration must cover meaningful
            // ground.
            if (inliers.Count < 2) continue;
            double minX = inliers.Min(a => a.PixelX), maxX = inliers.Max(a => a.PixelX);
            double minY = inliers.Min(a => a.PixelY), maxY = inliers.Max(a => a.PixelY);
            if (Math.Max(maxX - minX, maxY - minY) < 100) continue;

            // Score the candidate: prefer more inliers, but tie-break by the
            // refit residual over those inliers. A "wrong" seed can collect
            // inliers within the threshold window by chance — the refit over
            // those mis-paired points yields a high residual, while a
            // "correct" seed with the same inlier count refits to near-zero
            // residual.
            var refitRefs = inliers
                .Select(a => new LandmarkCalibrationSolver.Reference(a.WorldX, a.WorldZ, new PixelPoint(a.PixelX, a.PixelY)))
                .ToList();
            var refit = LandmarkCalibrationSolver.Solve(refitRefs);
            if (refit is null) continue;

            bool wins = inliers.Count > bestInlierCount
                     || (inliers.Count == bestInlierCount && refit.ResidualPixels < bestResidual);
            if (wins)
            {
                bestInlierCount = inliers.Count;
                bestResidual = refit.ResidualPixels;
                bestAssigned = inliers;
            }
        }

        if (bestInlierCount > 0)
        {
            Console.WriteLine($"[ransac] best seed had {bestInlierCount} inliers out of {pool.Count} pool entries");
            foreach (var a in bestAssigned)
            {
                Console.WriteLine($"  {a.Label}  world=({a.WorldX:0.0},{a.WorldZ:0.0}) tex=({a.PixelX:0.0},{a.PixelY:0.0}) score={a.MatchScore:0.000}");
            }
        }
        return bestAssigned;
    }

    private static (Dictionary<string, List<TypedDetection>> ByType, List<(IconMeta Icon, Detection Det, int RenderW, int RenderH)> Raw)
        DetectIconsByType(GrayImage screenshot, string iconsDir, IconIndex iconIndex, double threshold, int iconRenderSizeOverride)
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
        int chosenSize;
        if (iconRenderSizeOverride > 0)
        {
            chosenSize = iconRenderSizeOverride;
            Console.WriteLine($"[scale] using --icon-render-size override: {chosenSize} px");
        }
        else if (needsScaleSearch)
        {
            chosenSize = SelectGlobalRenderSize(screenshot, templates, [12, 16, 20, 24, 30, 40, 56], threshold);
        }
        else
        {
            chosenSize = 0;  // 0 = use each template's native size
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
            // No maxResults cap: NCC at 19 px on a complex map finds 100s of
            // above-threshold patches per template. A 64-cap by score loses
            // real matches that score lower than a glut of false-positive
            // teardrop-shaped terrain patches. RANSAC handles the noise
            // gracefully (geometric consistency wins) but it needs the real
            // matches to be IN the pool to consider them.
            var hits = NccTemplateMatch.FindAll(screenshot, grayD, alphaD, threshold, maxResults: null);
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

#pragma warning disable IDE0051  // kept for potential future per-type fallback
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
#pragma warning restore IDE0051
}
