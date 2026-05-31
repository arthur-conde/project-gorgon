using System;
using System.IO;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Mithril.MapCalibration.Detection;
using Mithril.MapCalibration.DependencyInjection;
using Xunit;

namespace Mithril.MapCalibration.Tests.Detection;

public sealed class EngineRegistrationTests
{
    // #931: the engine registration now takes the asset cache dir. An absent/empty
    // dir is the intended fail-soft (Empty template set + null-returning base
    // texture provider); the registration shape + singleton behaviour is what
    // these tests assert.
    private static string TempCacheDir() => Path.Combine(Path.GetTempPath(), "mithril931-engine-" + Guid.NewGuid().ToString("N"));

    [Fact]
    public void Resolves_solve_engine_and_dependencies()
    {
        var provider = new ServiceCollection()
            .AddMithrilMapCalibrationEngine(TempCacheDir())
            .BuildServiceProvider();

        provider.GetService<MapCalibrationSolveEngine>().Should().NotBeNull();
        provider.GetService<ICalibrationDetector>().Should().BeOfType<DeviationBlobCalibrationDetector>();
        provider.GetService<ICalibrationConfidenceGate>().Should().BeOfType<CalibrationConfidenceGate>();
        provider.GetService<IconTemplateSet>().Should().NotBeNull();
        provider.GetService<IBaseTextureProvider>().Should().NotBeNull();
    }

    [Fact]
    public void Template_set_is_singleton()
    {
        var provider = new ServiceCollection()
            .AddMithrilMapCalibrationEngine(TempCacheDir())
            .BuildServiceProvider();

        var a = provider.GetRequiredService<IconTemplateSet>();
        var b = provider.GetRequiredService<IconTemplateSet>();
        a.Should().BeSameAs(b);
    }

    [Fact]
    public void Absent_cache_dir_yields_empty_template_set_and_null_base_texture()
    {
        var provider = new ServiceCollection()
            .AddMithrilMapCalibrationEngine(TempCacheDir())
            .BuildServiceProvider();

        provider.GetRequiredService<IconTemplateSet>().Templates.Should().BeEmpty();
        provider.GetRequiredService<IBaseTextureProvider>().TryGetBaseTexture("AreaSerbule").Should().BeNull();
    }

    [Fact]
    public void Requires_a_cache_dir()
    {
        var act = () => new ServiceCollection().AddMithrilMapCalibrationEngine("");
        act.Should().Throw<ArgumentException>();
    }
}
