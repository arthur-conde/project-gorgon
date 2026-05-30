using Mithril.MapCalibration;

namespace Mithril.Tools.MapCalibrationStudy;

/// <summary>
/// (H3) Measures how the landmark world bounding-box projects into the texture:
/// the scale you would predict assuming the bbox fills the texture
/// (texture_dim / world_span), the ratio of that to the solved scale, and the
/// fractional margin (inset) between the projected bbox and the texture edges.
/// A constant inset fraction across areas is what makes scale computable cold.
/// </summary>
public static class InsetMetrics
{
    public readonly record struct Result(
        double PredictedScaleX,
        double PredictedScaleZ,
        double ScaleRatioX,
        double ScaleRatioZ,
        double InsetFracLeft,
        double InsetFracRight,
        double InsetFracTop,
        double InsetFracBottom)
    {
        public double InsetFracMax =>
            Math.Max(Math.Max(InsetFracLeft, InsetFracRight),
                     Math.Max(InsetFracTop, InsetFracBottom));
    }

    public static Result Compute(
        AreaCalibration cal, IReadOnlyList<WorldCoord> world, int textureW, int textureH)
    {
        if (world.Count < 2)
            throw new ArgumentException("need >= 2 world points for a bbox", nameof(world));

        double minX = double.MaxValue, maxX = double.MinValue;
        double minZ = double.MaxValue, maxZ = double.MinValue;
        double pMinX = double.MaxValue, pMaxX = double.MinValue;
        double pMinY = double.MaxValue, pMaxY = double.MinValue;
        foreach (var w in world)
        {
            minX = Math.Min(minX, w.X); maxX = Math.Max(maxX, w.X);
            minZ = Math.Min(minZ, w.Z); maxZ = Math.Max(maxZ, w.Z);
            var p = cal.WorldToWindow(w);
            pMinX = Math.Min(pMinX, p.X); pMaxX = Math.Max(pMaxX, p.X);
            pMinY = Math.Min(pMinY, p.Y); pMaxY = Math.Max(pMaxY, p.Y);
        }

        var spanX = maxX - minX;
        var spanZ = maxZ - minZ;
        var predX = spanX > 1e-9 ? textureW / spanX : 0.0;
        var predZ = spanZ > 1e-9 ? textureH / spanZ : 0.0;

        return new Result(
            PredictedScaleX: predX,
            PredictedScaleZ: predZ,
            ScaleRatioX: predX > 1e-9 ? cal.Scale / predX : 0.0,
            ScaleRatioZ: predZ > 1e-9 ? cal.Scale / predZ : 0.0,
            InsetFracLeft: pMinX / textureW,
            InsetFracRight: (textureW - pMaxX) / textureW,
            InsetFracTop: pMinY / textureH,
            InsetFracBottom: (textureH - pMaxY) / textureH);
    }
}
