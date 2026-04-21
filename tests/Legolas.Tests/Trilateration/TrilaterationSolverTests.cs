using FluentAssertions;
using Legolas.Domain;
using Legolas.Services;

namespace Legolas.Tests.Trilateration;

public class TrilaterationSolverTests
{
    private readonly TrilaterationSolver _solver = new();

    [Fact]
    public void Finds_known_intersection()
    {
        var target = new PixelPoint(5, 7);
        var p1 = new PixelPoint(0, 0);
        var p2 = new PixelPoint(10, 0);
        var p3 = new PixelPoint(0, 10);
        var r1 = p1.DistanceTo(target);
        var r2 = p2.DistanceTo(target);
        var r3 = p3.DistanceTo(target);

        var result = _solver.Solve(p1, r1, p2, r2, p3, r3);

        result.Should().NotBeNull();
        result!.Value.X.Should().BeApproximately(target.X, 1e-6);
        result.Value.Y.Should().BeApproximately(target.Y, 1e-6);
    }

    [Fact]
    public void Collinear_points_return_null()
    {
        var p1 = new PixelPoint(0, 0);
        var p2 = new PixelPoint(5, 0);
        var p3 = new PixelPoint(10, 0);

        var result = _solver.Solve(p1, 5, p2, 5, p3, 5);

        result.Should().BeNull();
    }
}
