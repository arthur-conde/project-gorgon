using Mithril.MapCalibration;

namespace Mithril.Tools.MapCalibrationStudy;

/// <summary>
/// (H4) The decisive proof: from ONLY world landmark coordinates + texture
/// dimensions + detected icon pixels (no stored calibration, no manual clicks),
/// recover the renderer transform. Enumerates the 4 axis-aligned orientation
/// states (±X × ±Z), estimates a rough scale-from-bbox per state, corresponds
/// each predicted landmark to its nearest detected icon, solves via the shipped
/// similarity solver (which re-confirms handedness), and keeps the best-fitting
/// orientation after an outlier-guard pass. Informational prototype of the
/// engine's correspondence step — not the engine itself.
/// </summary>
public static class ColdBootstrap
{
    public sealed record Result(
        AreaCalibration Calibration,
        double RefinedResidualPx,
        int CorrespondedCount,
        bool MirrorX,
        bool MirrorZ);

    public static Result? Run(
        IReadOnlyList<WorldCoord> world,
        IReadOnlyList<PixelPoint> detected,
        int textureW,
        int textureH,
        double axisThresholdPx)
    {
        if (world.Count < 3 || detected.Count < 3) return null;

        double minX = world.Min(w => w.X), maxX = world.Max(w => w.X);
        double minZ = world.Min(w => w.Z), maxZ = world.Max(w => w.Z);
        var spanX = Math.Max(1e-9, maxX - minX);
        var spanZ = Math.Max(1e-9, maxZ - minZ);
        // Rough isotropic scale: the smaller predicted scale avoids over-shooting
        // when one axis is inset more than the other.
        var scale0 = Math.Min(textureW / spanX, textureH / spanZ);

        Result? best = null;
        foreach (var mirrorX in new[] { false, true })
        foreach (var mirrorZ in new[] { false, true })
        {
            // Predict each landmark's texture pixel under this orientation +
            // rough scale, centring the world bbox in the texture so nearest-
            // neighbour correspondence is meaningful before the real solve.
            var cxWorld = (minX + maxX) / 2.0;
            var czWorld = (minZ + maxZ) / 2.0;
            PixelPoint Predict(WorldCoord w)
            {
                var x = (mirrorX ? -1 : 1) * (w.X - cxWorld);
                var z = (mirrorZ ? -1 : 1) * (w.Z - czWorld);
                return new PixelPoint(textureW / 2.0 + scale0 * x, textureH / 2.0 - scale0 * z);
            }

            var refs = new List<LandmarkCalibrationSolver.Reference>();
            var used = new bool[detected.Count];
            var corresponded = 0;
            foreach (var w in world)
            {
                var pred = Predict(w);
                var bestIdx = -1; var bestD = double.MaxValue;
                for (var i = 0; i < detected.Count; i++)
                {
                    if (used[i]) continue;
                    var dx = detected[i].X - pred.X;
                    var dy = detected[i].Y - pred.Y;
                    var d = dx * dx + dy * dy;
                    if (d < bestD) { bestD = d; bestIdx = i; }
                }
                if (bestIdx < 0) continue;
                used[bestIdx] = true;
                corresponded++;
                refs.Add(new LandmarkCalibrationSolver.Reference(w.X, w.Z, detected[bestIdx]));
            }

            if (refs.Count < 3) continue;
            var kept = OutlierGuard.Reject(refs, axisThresholdPx);
            var cal = LandmarkCalibrationSolver.Solve(kept);
            if (cal is null) continue;

            // Several enumerated orientations can tie at (near-)zero residual:
            // the shipped solver internally re-confirms handedness, so even a
            // "wrong" enumerated mirror fits a self-consistent subset perfectly.
            // Break residual ties by preferring the orientation that retained
            // MORE references through the outlier guard — a perfect fit on 6
            // inliers beats a perfect fit on 4 (the latter mispaired two icons
            // and the guard discarded them). Use a small epsilon so a marginally
            // larger residual with more inliers still wins.
            if (best is null
                || cal.ResidualPixels < best.RefinedResidualPx - 1e-6
                || (cal.ResidualPixels <= best.RefinedResidualPx + 1e-6
                    && kept.Count > best.CorrespondedCount))
                best = new Result(cal, cal.ResidualPixels, kept.Count, mirrorX, mirrorZ);
        }

        return best;
    }
}
