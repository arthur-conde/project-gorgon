using FluentAssertions;
using Mithril.MapCalibration.Detection;
using Xunit;

namespace Mithril.MapCalibration.Tests.Detection;

public sealed class CalibrationConfidenceGateTests
{
    private static AreaCalibration Cal(double residual) =>
        new(Scale: 1.2, RotationRadians: 0.0, OriginX: 0, OriginY: 0, ReferenceCount: 8, ResidualPixels: residual);

    [Fact]
    public void Accepts_low_residual_high_inliers()
    {
        var gate = new CalibrationConfidenceGate();
        gate.Accept(Cal(0.7), inlierCount: 8, out var reason).Should().BeTrue();
        reason.Should().BeNull();
    }

    [Fact]
    public void Rejects_high_residual()
    {
        var gate = new CalibrationConfidenceGate();
        gate.Accept(Cal(25.0), inlierCount: 8, out var reason).Should().BeFalse();
        reason.Should().NotBeNullOrEmpty();
    }

    [Fact]
    public void Rejects_too_few_inliers()
    {
        var gate = new CalibrationConfidenceGate();
        gate.Accept(Cal(0.7), inlierCount: 2, out var reason).Should().BeFalse();
        reason.Should().NotBeNullOrEmpty();
    }

    [Fact]
    public void Honours_configured_threshold_and_floor()
    {
        var gate = new CalibrationConfidenceGate(goodResidualThresholdPx: 5.0, inlierFloor: 6);
        gate.Accept(Cal(4.0), inlierCount: 6, out _).Should().BeTrue();
        gate.Accept(Cal(4.0), inlierCount: 5, out _).Should().BeFalse();
        gate.Accept(Cal(6.0), inlierCount: 6, out _).Should().BeFalse();
    }
}
