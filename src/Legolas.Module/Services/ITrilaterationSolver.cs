using Legolas.Domain;

namespace Legolas.Services;

public interface ITrilaterationSolver
{
    PixelPoint? Solve(PixelPoint p1, double r1, PixelPoint p2, double r2, PixelPoint p3, double r3);
}
