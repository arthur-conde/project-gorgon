using Mithril.MapCalibration;

namespace Mithril.Tools.MapCalibrationStudy;

/// <summary>
/// (H2) Isotropy check. Fits a full 6-parameter affine
/// (px = a·wx + b·wz + c; py = d·wx + e·wz + f) by least squares and reports
/// its RMS pixel residual, alongside the constrained 4-param similarity RMS
/// (via the shipped <see cref="LandmarkCalibrationSolver"/>). If the affine
/// barely beats the similarity on real data, the renderer is isotropic.
/// </summary>
public static class AffineFit
{
    public static double Rms(IReadOnlyList<(double wx, double wz, double px, double py)> pts)
    {
        if (pts.Count < 3) throw new ArgumentException("affine needs >= 3 points", nameof(pts));
        // Two independent 3-param normal-equation solves (X then Y) on basis
        // [wx, wz, 1]. Symmetric 3x3 system; solved by Cramer's rule.
        var (ax, bx, cx) = Solve3(pts, forX: true);
        var (ay, by, cy) = Solve3(pts, forX: false);
        double sumSq = 0;
        foreach (var (wx, wz, px, py) in pts)
        {
            var ex = ax * wx + bx * wz + cx - px;
            var ey = ay * wx + by * wz + cy - py;
            sumSq += ex * ex + ey * ey;
        }
        return Math.Sqrt(sumSq / pts.Count);
    }

    public static double SimilarityRms(IReadOnlyList<(double wx, double wz, double px, double py)> pts)
    {
        var refs = pts
            .Select(p => new LandmarkCalibrationSolver.Reference(p.wx, p.wz, new PixelPoint(p.px, p.py)))
            .ToList();
        var cal = LandmarkCalibrationSolver.Solve(refs);
        return cal?.ResidualPixels ?? double.PositiveInfinity;
    }

    private static (double a, double b, double c) Solve3(
        IReadOnlyList<(double wx, double wz, double px, double py)> pts, bool forX)
    {
        // Normal equations for minimising Σ (a·wx + b·wz + c − t)².
        double Sxx = 0, Sxz = 0, Sx1 = 0, Szz = 0, Sz1 = 0, S11 = 0;
        double Sxt = 0, Szt = 0, S1t = 0;
        foreach (var (wx, wz, px, py) in pts)
        {
            var t = forX ? px : py;
            Sxx += wx * wx; Sxz += wx * wz; Sx1 += wx;
            Szz += wz * wz; Sz1 += wz; S11 += 1;
            Sxt += wx * t; Szt += wz * t; S1t += t;
        }
        // 3x3 symmetric matrix M = [[Sxx,Sxz,Sx1],[Sxz,Szz,Sz1],[Sx1,Sz1,S11]],
        // rhs = [Sxt,Szt,S1t]. Cramer's rule.
        double Det(double a, double b, double c, double d, double e, double f, double g, double h, double i)
            => a * (e * i - f * h) - b * (d * i - f * g) + c * (d * h - e * g);

        var det = Det(Sxx, Sxz, Sx1, Sxz, Szz, Sz1, Sx1, Sz1, S11);
        if (Math.Abs(det) < 1e-12) return (0, 0, S1t / Math.Max(1, S11)); // degenerate fallback
        var a = Det(Sxt, Sxz, Sx1, Szt, Szz, Sz1, S1t, Sz1, S11) / det;
        var b = Det(Sxx, Sxt, Sx1, Sxz, Szt, Sz1, Sx1, S1t, S11) / det;
        var c = Det(Sxx, Sxz, Sxt, Sxz, Szz, Szt, Sx1, Sz1, S1t) / det;
        return (a, b, c);
    }
}
