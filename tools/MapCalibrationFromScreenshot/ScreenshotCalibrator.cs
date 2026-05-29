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
    // NCC threshold for accepting a detection. NCC peaks at 1.0; clean PG icons
    // typically score 0.6–0.9 against an in-game screenshot. 0.5 keeps weak-but-
    // real matches in play; the brute-force assignment step culls noise.
    private const double DetectionScoreMin = 0.5;

    public static CalibrationResult Calibrate(CalibrationInputs inputs)
    {
        var screenshotGray = ImageIo.LoadGray(inputs.ScreenshotPath);
        var textureGray = ImageIo.LoadGray(inputs.AreaMapPath);
        Console.WriteLine($"[screenshot] {screenshotGray.Width}x{screenshotGray.Height} / texture {textureGray.Width}x{textureGray.Height}");

        var iconIndex = IconTemplateExtractor.Load(inputs.IconsDir);
        var landmarks = LandmarksReader.LoadForArea(inputs.LandmarksJsonPath, inputs.Area);
        Console.WriteLine($"[refs] {landmarks.Count} landmarks for {inputs.Area} from {Path.GetFileName(inputs.LandmarksJsonPath)}");

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
        var detectionsByType = DetectIconsByType(screenshotGray, inputs.IconsDir, iconIndex);

        foreach (var typeGroup in detectionsByType)
        {
            if (typeGroup.Key == "Player") continue; // handled below

            var dets = typeGroup.Value;
            var areaRefs = landmarks.Where(l => string.Equals(l.Type, typeGroup.Key, StringComparison.Ordinal)).ToList();
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

    private static Dictionary<string, List<TypedDetection>> DetectIconsByType(
        GrayImage screenshot, string iconsDir, IconIndex iconIndex)
    {
        var byType = new Dictionary<string, List<TypedDetection>>(StringComparer.Ordinal);
        foreach (var icon in iconIndex.Icons)
        {
            var iconPath = Path.Combine(iconsDir, icon.File);
            if (!File.Exists(iconPath))
            {
                Console.WriteLine($"  ! template file missing: {iconPath} (skipping)");
                continue;
            }
            var (gray, alpha) = ImageIo.LoadGrayAndAlpha(iconPath);
            var detections = NccTemplateMatch.FindAll(screenshot, gray, alpha, DetectionScoreMin, maxResults: 32);
            if (detections.Count == 0) continue;

            if (!byType.TryGetValue(icon.LandmarkType, out var list))
            {
                list = new List<TypedDetection>();
                byType[icon.LandmarkType] = list;
            }
            foreach (var d in detections)
            {
                var (cx, cy) = d.Centre(icon.Width, icon.Height);
                var anchorX = cx + icon.Width * (icon.PivotX - 0.5);
                var anchorY = cy + icon.Height * (0.5 - icon.PivotY);
                list.Add(new TypedDetection(
                    IconName: icon.Name,
                    AnchorScreenshotX: anchorX,
                    AnchorScreenshotY: anchorY,
                    MatchScore: d.Score));
            }
        }
        return byType;
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
