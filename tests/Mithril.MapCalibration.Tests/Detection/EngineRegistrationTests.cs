using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Mithril.MapCalibration.Detection;
using Mithril.MapCalibration.DependencyInjection;
using Xunit;

namespace Mithril.MapCalibration.Tests.Detection;

public sealed class EngineRegistrationTests
{
    [Fact]
    public void Resolves_solve_engine_and_dependencies()
    {
        var provider = new ServiceCollection()
            .AddMithrilMapCalibrationEngine()
            .BuildServiceProvider();

        provider.GetService<MapCalibrationSolveEngine>().Should().NotBeNull();
        provider.GetService<ICalibrationDetector>().Should().BeOfType<DeviationBlobCalibrationDetector>();
        provider.GetService<ICalibrationConfidenceGate>().Should().BeOfType<CalibrationConfidenceGate>();
        provider.GetService<IconTemplateSet>().Should().NotBeNull();
    }

    [Fact]
    public void Template_set_is_singleton()
    {
        var provider = new ServiceCollection()
            .AddMithrilMapCalibrationEngine()
            .BuildServiceProvider();

        var a = provider.GetRequiredService<IconTemplateSet>();
        var b = provider.GetRequiredService<IconTemplateSet>();
        a.Should().BeSameAs(b);
    }
}
