using Legolas.Domain;

namespace Legolas.Services;

public sealed class TrilaterationSolver : ITrilaterationSolver
{
    private const double CollinearityTolerance = 1e-6;

    public PixelPoint? Solve(PixelPoint p1, double r1, PixelPoint p2, double r2, PixelPoint p3, double r3)
    {
        // Subtract circle equations pairwise to produce two linear equations in (x, y).
        var a1 = 2 * (p2.X - p1.X);
        var b1 = 2 * (p2.Y - p1.Y);
        var d1 = (p2.X * p2.X - p1.X * p1.X)
               + (p2.Y * p2.Y - p1.Y * p1.Y)
               - (r2 * r2 - r1 * r1);

        var a2 = 2 * (p3.X - p1.X);
        var b2 = 2 * (p3.Y - p1.Y);
        var d2 = (p3.X * p3.X - p1.X * p1.X)
               + (p3.Y * p3.Y - p1.Y * p1.Y)
               - (r3 * r3 - r1 * r1);

        var det = a1 * b2 - a2 * b1;
        if (Math.Abs(det) < CollinearityTolerance)
        {
            return null;
        }

        var x = (d1 * b2 - d2 * b1) / det;
        var y = (a1 * d2 - a2 * d1) / det;
        return new PixelPoint(x, y);
    }
}
