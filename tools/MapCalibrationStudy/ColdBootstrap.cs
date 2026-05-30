using Mithril.MapCalibration;

namespace Mithril.Tools.MapCalibrationStudy;

/// <summary>
/// (H4) The decisive proof: from ONLY world landmark coordinates + texture
/// dimensions + detected icon pixels (no stored calibration, no manual clicks),
/// recover the renderer transform. Enumerates the 4 axis-aligned orientation
/// states (±X × ±Z), estimates a rough scale-from-bbox per state, corresponds
/// each predicted landmark to its nearest detected icon, solves via the shipped
/// similarity solver (which re-confirms handedness) after an outlier-guard pass,
/// and selects the orientation by GLOBAL reprojection consistency over the full
/// detection cloud (see <see cref="Result.GlobalReprojectionPx"/>) — not the
/// kept-subset solver residual, which a reflected orientation can also drive to
/// zero on a different subset. Informational prototype of the engine's
/// correspondence step — not the engine itself.
/// </summary>
public static class ColdBootstrap
{
    public sealed record Result(
        AreaCalibration Calibration,
        double RefinedResidualPx,
        int CorrespondedCount,
        bool MirrorX,
        bool MirrorZ)
    {
        /// <summary>
        /// MEAN over ALL world landmarks of the distance from the recovered
        /// transform's reprojection to its nearest detected icon. Unlike
        /// <see cref="RefinedResidualPx"/> (which is the solver residual over the
        /// kept SUBSET, and is near-zero even for a mispaired orientation that
        /// fit a different self-consistent subset), this scores the whole set
        /// against the whole detection cloud — so a reflected orientation that
        /// reprojects even a FEW landmarks onto the WRONG icons is penalised
        /// (mean, not median, so a minority of mispairs still moves the score).
        /// Selection minimises this first; the recovered transform reproduces
        /// ground truth only when it is small.
        /// </summary>
        public double GlobalReprojectionPx { get; init; }

        /// <summary>
        /// (H2) RMS pixel residual of a full 6-parameter affine fit over the
        /// SAME kept-inlier (world ↔ detected-pixel) pairs the 4-param similarity
        /// (<see cref="RefinedResidualPx"/>) was solved on — an apples-to-apples
        /// affine-vs-similarity contest on identical points. This is the ONLY
        /// place H2 is honestly measurable: bootstrap has independent detected
        /// pixels, whereas measure mode would have to reproject through the
        /// solved similarity (degenerate). If the affine barely beats the
        /// similarity here, the renderer is isotropic.
        /// </summary>
        public double AffineResidualPx { get; init; }
    }

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
                refs.Add(new LandmarkCalibrationSolver.Reference(w.X, w.Z, detected[bestIdx]));
            }

            if (refs.Count < 3) continue;
            var kept = OutlierGuard.Reject(refs, axisThresholdPx);
            var cal = LandmarkCalibrationSolver.Solve(kept);
            if (cal is null) continue;

            // Score by GLOBAL reprojection consistency over the FULL detected
            // set, NOT just the kept-subset solver residual. The subset residual
            // is blind to the failure mode the reviewer found: because the
            // shipped solver internally re-confirms handedness, a "wrong"
            // enumerated mirror can mispair landmarks onto reflection-partner
            // icons and still fit that DIFFERENT subset at ~0 residual. The
            // global score re-corresponds every world landmark to its nearest
            // detected icon under THIS orientation's solved transform; a
            // reflected orientation reprojects onto the wrong icons (or far from
            // any), inflating the score, while the true orientation reprojects
            // the whole set consistently. Minimise the global score; break exact
            // ties by inlier count. (A genuinely point-symmetric area is truly
            // 0°/180° ambiguous — both orientations score equally and the first
            // enumerated wins; real PG areas aren't point-symmetric.)
            var globalPx = GlobalReprojection(cal, world, detected);
            // H2: fit a 6-param affine over the SAME kept-inlier pairs the
            // similarity was solved on, so the affine-vs-similarity comparison is
            // on identical points. Needs >= 3 pairs (affine has 6 DOF / 3 points
            // per axis); the kept set is already guaranteed >= 3 above.
            var affinePts = kept
                .Select(r => (r.WorldX, r.WorldZ, r.Pixel.X, r.Pixel.Y))
                .ToList();
            var affineRms = affinePts.Count >= 3 ? AffineFit.Rms(affinePts) : double.NaN;
            var candidate = new Result(cal, cal.ResidualPixels, kept.Count, mirrorX, mirrorZ)
            {
                GlobalReprojectionPx = globalPx,
                AffineResidualPx = affineRms,
            };
            if (best is null
                || candidate.GlobalReprojectionPx < best.GlobalReprojectionPx - 1e-6
                || (candidate.GlobalReprojectionPx <= best.GlobalReprojectionPx + 1e-6
                    && candidate.CorrespondedCount > best.CorrespondedCount))
                best = candidate;
        }

        return best;
    }

    /// <summary>
    /// MEAN over all world landmarks of the distance from
    /// <c>cal.WorldToWindow(w)</c> to its nearest detected icon. Mean — not
    /// median — because the failure mode is a MINORITY of mispaired landmarks: a
    /// reflected orientation can mispair just the interior points while the
    /// corners still reproject at distance 0, so a median washes the mispairs out
    /// (it reads 0 for every orientation and the metric becomes decorative). The
    /// mean keeps the mispaired-landmark distances in the score, so the true
    /// orientation (every landmark at ~0) separates from a reflected one (a few
    /// landmarks far away) — making the selection genuinely load-bearing rather
    /// than reliant on the secondary inlier-count tie-break.
    /// </summary>
    private static double GlobalReprojection(
        AreaCalibration cal, IReadOnlyList<WorldCoord> world, IReadOnlyList<PixelPoint> detected)
    {
        if (world.Count == 0) return double.PositiveInfinity;
        double sum = 0;
        foreach (var w in world)
        {
            var p = cal.WorldToWindow(w);
            var nearest = double.MaxValue;
            foreach (var d in detected)
            {
                var dx = d.X - p.X;
                var dy = d.Y - p.Y;
                var dist = Math.Sqrt(dx * dx + dy * dy);
                if (dist < nearest) nearest = dist;
            }
            sum += nearest;
        }
        return sum / world.Count;
    }
}
