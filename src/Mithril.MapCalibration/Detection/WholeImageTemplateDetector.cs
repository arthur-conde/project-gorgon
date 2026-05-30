using System;
using System.Collections.Generic;

namespace Mithril.MapCalibration.Detection;

/// <summary>
/// Fallback detector: whole-image template NCC per icon (no deviation map). Picks
/// a single global render size across the template set (PG renders all map icons
/// at one consistent on-screen pixel size), then runs mask-aware NCC over the
/// whole cropped map and pivot-corrects each hit to a screenshot-space anchor.
///
/// <para>Ported from the gate-study <c>ScreenshotCalibrator.DetectIconsByType</c>
/// + <c>SelectGlobalRenderSize</c>. The spec keeps both paths (§8); the
/// deviation-blob detector is preferred on sparse irregular-bordered areas, this
/// one is the simpler dense-area fallback. BCL-only.</para>
/// </summary>
public sealed class WholeImageTemplateDetector : ICalibrationDetector
{
    // Render-size ladder swept when templates are large (PG artwork ~256 px).
    private static readonly int[] RenderSizeLadder = [12, 16, 20, 24, 30, 40, 56];

    public IReadOnlyDictionary<string, IReadOnlyList<TypedDetection>> Detect(DetectionRequest request)
    {
        var screenshot = request.Screenshot;
        var templates = request.Templates.Templates;
        double threshold = request.TypeFloor;

        int maxTemplateDim = 0;
        foreach (var t in templates) maxTemplateDim = Math.Max(maxTemplateDim, Math.Max(t.Gray.Width, t.Gray.Height));
        bool needsScaleSearch = maxTemplateDim > 64;
        int chosenSize = needsScaleSearch
            ? SelectGlobalRenderSize(screenshot, templates, RenderSizeLadder, threshold)
            : 0; // 0 = native per-template dims

        var byType = new Dictionary<string, List<TypedDetection>>(StringComparer.Ordinal);
        foreach (var icon in templates)
        {
            int rw, rh;
            if (chosenSize == 0)
            {
                rw = icon.Gray.Width; rh = icon.Gray.Height;
            }
            else
            {
                int maxDim = Math.Max(icon.Gray.Width, icon.Gray.Height);
                rw = Math.Max(1, icon.Gray.Width * chosenSize / maxDim);
                rh = Math.Max(1, icon.Gray.Height * chosenSize / maxDim);
            }
            var grayD = (rw == icon.Gray.Width && rh == icon.Gray.Height) ? icon.Gray : ImageOps.Resize(icon.Gray, rw, rh);
            var alphaD = (rw == icon.Alpha.Width && rh == icon.Alpha.Height) ? icon.Alpha : ImageOps.Resize(icon.Alpha, rw, rh);

            // 64-cap per template by score — a quality filter against the
            // mid-score false-positive flood (gate-study finding).
            var hits = NccTemplateMatch.FindAll(screenshot, grayD, alphaD, threshold, maxResults: 64);
            if (hits.Count == 0) continue;

            if (!byType.TryGetValue(icon.LandmarkType, out var list))
            {
                list = new List<TypedDetection>();
                byType[icon.LandmarkType] = list;
            }
            foreach (var hit in hits)
            {
                var (cx, cy) = hit.Centre(rw, rh);
                double anchorX = cx + rw * (icon.PivotX - 0.5);
                double anchorY = cy + rh * (0.5 - icon.PivotY);
                list.Add(new TypedDetection(icon.LandmarkType, icon.Name, anchorX, anchorY, hit.Score));
            }
        }

        var result = new Dictionary<string, IReadOnlyList<TypedDetection>>(byType.Count, StringComparer.Ordinal);
        foreach (var kv in byType) result[kv.Key] = kv.Value;
        return result;
    }

    private static int SelectGlobalRenderSize(
        GrayImage screenshot, IReadOnlyList<IconTemplate> templates, int[] candidates, double threshold)
    {
        if (candidates.Length == 1) return candidates[0];

        int best = candidates[0];
        double bestEvidence = double.NegativeInfinity;
        foreach (var target in candidates)
        {
            double evidence = 0;
            foreach (var t in templates)
            {
                int maxDim = Math.Max(t.Gray.Width, t.Gray.Height);
                int rw = Math.Max(1, t.Gray.Width * target / maxDim);
                int rh = Math.Max(1, t.Gray.Height * target / maxDim);
                var grayD = ImageOps.Resize(t.Gray, rw, rh);
                var alphaD = ImageOps.Resize(t.Alpha, rw, rh);
                var top = NccTemplateMatch.FindBest(screenshot, grayD, alphaD, threshold);
                if (top is null) continue;
                evidence += top.Value.Score;
            }
            if (evidence > bestEvidence)
            {
                bestEvidence = evidence;
                best = target;
            }
        }
        return best;
    }
}
