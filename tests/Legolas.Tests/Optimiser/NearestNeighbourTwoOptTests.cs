using FluentAssertions;
using Legolas.Domain;
using Legolas.Services;

namespace Legolas.Tests.Optimiser;

public class NearestNeighbourTwoOptTests
{
    private readonly NearestNeighbourTwoOptOptimizer _optimiser = new();

    [Fact]
    public void Empty_input_returns_empty_route()
    {
        _optimiser.Optimize(PixelPoint.Zero, Array.Empty<PixelPoint>())
            .Should().BeEmpty();
    }

    [Fact]
    public void Single_point_returns_zero()
    {
        _optimiser.Optimize(PixelPoint.Zero, new[] { new PixelPoint(1, 1) })
            .Should().Equal(0);
    }

    [Fact]
    public void Visits_all_points_once_without_duplicates()
    {
        var rng = new Random(Seed: 7);
        var points = Enumerable.Range(0, 12)
            .Select(_ => new PixelPoint(rng.NextDouble() * 500, rng.NextDouble() * 500))
            .ToArray();

        var route = _optimiser.Optimize(PixelPoint.Zero, points);

        route.Should().HaveCount(points.Length);
        route.Should().OnlyHaveUniqueItems();
        route.Should().AllSatisfy(i => i.Should().BeInRange(0, points.Length - 1));
    }

    [Fact]
    public void Collinear_points_are_ordered_by_distance()
    {
        var points = new[]
        {
            new PixelPoint(3, 0),
            new PixelPoint(1, 0),
            new PixelPoint(5, 0),
            new PixelPoint(2, 0),
        };

        var route = _optimiser.Optimize(PixelPoint.Zero, points);

        route.Should().Equal(1, 3, 0, 2);
    }
}
