using FluentAssertions;
using Legolas.Domain;

namespace Legolas.Tests.Domain;

public class WorldCoordTests
{
    [Theory]
    [InlineData("x:1138.161865 y:39.093884 z:1367.77417", 1138.161865, 39.093884, 1367.77417)]
    [InlineData("x:-723.900024 y:71.419998 z:-399.079987", -723.900024, 71.419998, -399.079987)]
    [InlineData("z:10 y:2 x:-5", -5, 2, 10)] // tokens out of order
    [InlineData("1.5 2 -3.25", 1.5, 2, -3.25)] // bare fallback
    public void Parses_canonical_and_fallback_forms(string loc, double x, double y, double z)
    {
        var w = WorldCoord.TryParse(loc);

        w.Should().NotBeNull();
        w!.Value.X.Should().BeApproximately(x, 1e-6);
        w.Value.Y.Should().BeApproximately(y, 1e-6);
        w.Value.Z.Should().BeApproximately(z, 1e-6);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("x:1 y:2")]          // missing z
    [InlineData("garbage")]
    [InlineData("x:foo y:2 z:3")]    // unparseable component
    public void Returns_null_when_three_components_do_not_resolve(string? loc)
    {
        WorldCoord.TryParse(loc).Should().BeNull();
    }
}
