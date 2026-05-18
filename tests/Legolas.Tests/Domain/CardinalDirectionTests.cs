using FluentAssertions;
using Legolas.Domain;

namespace Legolas.Tests.Domain;

public class CardinalDirectionTests
{
    // atan2(East, North), clockwise from map-north. North = +Z, East = +X.
    [Theory]
    [InlineData(0, 1, CardinalDirection.N)]    // due +North
    [InlineData(1, 1, CardinalDirection.NE)]
    [InlineData(1, 0, CardinalDirection.E)]    // due +East
    [InlineData(1, -1, CardinalDirection.SE)]
    [InlineData(0, -1, CardinalDirection.S)]   // due -North
    [InlineData(-1, -1, CardinalDirection.SW)]
    [InlineData(-1, 0, CardinalDirection.W)]   // due -East
    [InlineData(-1, 1, CardinalDirection.NW)]
    public void FromBearing_maps_each_octant(double east, double north, CardinalDirection expected)
    {
        CardinalDirectionExtensions.FromBearing(east, north).Should().Be(expected);
    }

    [Theory]
    [InlineData(CardinalDirection.N)]
    [InlineData(CardinalDirection.NE)]
    [InlineData(CardinalDirection.E)]
    [InlineData(CardinalDirection.SE)]
    [InlineData(CardinalDirection.S)]
    [InlineData(CardinalDirection.SW)]
    [InlineData(CardinalDirection.W)]
    [InlineData(CardinalDirection.NW)]
    public void FromBearing_round_trips_ToBearingRadians(CardinalDirection dir)
    {
        var o = dir.ToOffset(100);
        CardinalDirectionExtensions.FromBearing(o.East, o.North).Should().Be(dir);
    }

    [Fact]
    public void FromBearing_zero_vector_resolves_to_N()
    {
        CardinalDirectionExtensions.FromBearing(0, 0).Should().Be(CardinalDirection.N);
    }

    [Theory]
    // Sector boundaries round to the adjacent octant centre (away-from-zero).
    [InlineData(0.30, 0.99, CardinalDirection.N)]   // ~17° → N
    [InlineData(0.80, 0.60, CardinalDirection.NE)]  // ~53° → NE
    public void FromBearing_buckets_near_boundaries(double east, double north, CardinalDirection expected)
    {
        CardinalDirectionExtensions.FromBearing(east, north).Should().Be(expected);
    }
}
