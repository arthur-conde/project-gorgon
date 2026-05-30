using FluentAssertions;
using Mithril.Tools.MapCalibrationStudy;
using Xunit;

namespace Mithril.Tools.MapCalibrationStudy.Tests;

public class OrientationClassTests
{
    [Theory]
    [InlineData(0.00033359980485819676, 0)]     // AreaSerbule
    [InlineData(5.863329159379195e-06, 0)]       // AreaCave1
    [InlineData(-3.141529626160087, 180)]        // AreaEltibule ≈ -π
    [InlineData(3.1415789203621567, 180)]        // AreaKurMountains ≈ +π
    public void Classifies_to_nearest_axis_member(double radians, int expectedDeg)
    {
        OrientationClass.Classify(radians).NearestDeg.Should().Be(expectedDeg);
    }

    [Fact]
    public void Reports_small_deviation_for_on_axis_rotation()
    {
        var c = OrientationClass.Classify(0.00033359980485819676);
        c.DeviationDeg.Should().BeLessThan(0.05);
        c.InSet.Should().BeTrue();   // within tolerance of a member
    }

    [Fact]
    public void Flags_an_in_between_angle_as_out_of_set()
    {
        var c = OrientationClass.Classify(Math.PI / 4); // 45° — neither 0 nor 180
        c.NearestDeg.Should().Be(0);                    // 45 is closer to 0 than 180
        c.DeviationDeg.Should().BeApproximately(45, 0.01);
        c.InSet.Should().BeFalse();
    }
}
