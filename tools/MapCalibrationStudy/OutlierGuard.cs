using Mithril.MapCalibration;

namespace Mithril.Tools.MapCalibrationStudy;

/// <summary>
/// (H4 support) Rejects references whose fit residual is large in a single axis
/// only — the signature of a detection/transcription error rather than a real
/// map offset (a genuine offset shows up roughly isotropically). Iteratively
/// solves, finds the worst single-axis-asymmetric residual above the threshold,
/// drops it, and re-solves until none remain or too few refs survive.
/// </summary>
public static class OutlierGuard
{
    public static List<LandmarkCalibrationSolver.Reference> Reject(
        IReadOnlyList<LandmarkCalibrationSolver.Reference> refs, double axisThresholdPx)
    {
        var live = refs.ToList();
        while (live.Count > 3)
        {
            var cal = LandmarkCalibrationSolver.Solve(live);
            if (cal is null) break;

            int worst = -1;
            double worstAsymmetry = axisThresholdPx;
            for (var i = 0; i < live.Count; i++)
            {
                var p = cal.WorldToWindow(new WorldCoord(live[i].WorldX, 0, live[i].WorldZ));
                var dx = Math.Abs(p.X - live[i].Pixel.X);
                var dy = Math.Abs(p.Y - live[i].Pixel.Y);
                var maxAxis = Math.Max(dx, dy);
                var minAxis = Math.Min(dx, dy);
                // "One axis only" = the larger axis error clears the threshold
                // while the other stays small. Asymmetry = maxAxis - minAxis.
                var asymmetry = maxAxis - minAxis;
                if (maxAxis >= axisThresholdPx && asymmetry > worstAsymmetry)
                {
                    worstAsymmetry = asymmetry;
                    worst = i;
                }
            }

            if (worst < 0) break;
            live.RemoveAt(worst);
        }
        return live;
    }
}
