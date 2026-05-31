using System;
using System.IO;
using System.Linq;
using Arda.Contracts;
using Arda.World.Player;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Mithril.MapCalibration.Capture.DependencyInjection;
using Mithril.MapCalibration.Capture.Tests.Fixtures;
using Mithril.Overlay;
using Mithril.Shared.Game;
using Mithril.Shared.Hotkeys;
using Mithril.Shared.Reference;
using Xunit;

namespace Mithril.MapCalibration.Capture.Tests;

/// <summary>
/// Task 28 (#914): the auto-capture pipeline resolves from a built provider with
/// fakes for the cross-cutting collaborators it doesn't own (IOverlayWindow,
/// IDomainEventSubscriber, IAreaState, IReferenceDataService). Exercises the full
/// AddMithrilMapCalibrationCapture registration graph — including the
/// GameConfig-wired gate override and the asset-engine registration — so a
/// missing/mis-shaped registration fails here, and ValidateOnBuild catches a
/// singleton cycle (memory: di_cycle_invisible_to_unit_tests).
/// </summary>
public sealed class CaptureDependencyInjectionTests
{
    [Fact]
    public void Auto_capture_pipeline_resolves_from_a_built_provider()
    {
        var settingsDir = Path.Combine(Path.GetTempPath(), "mithril-capture-di-" + Guid.NewGuid());
        var assetCacheDir = Path.Combine(settingsDir, "assets");

        var services = new ServiceCollection();

        // The trigger is a hosted service → requires a non-optional ILogger
        // (CLAUDE.md), resolved via ILoggerFactory in the DI lambda. The shell
        // always registers logging; mirror that here so the graph resolves.
        services.AddLogging();

        // Cross-cutting collaborators the shell normally provides — faked here.
        services.AddSingleton(new GameConfig { CalibrationGoodResidualPx = 9.0, GameRoot = "" });
        services.AddSingleton<IOverlayWindow>(new FakeOverlayWindow());
        services.AddSingleton<IDomainEventSubscriber>(new FakeDomainEventSubscriber());
        services.AddSingleton<IAreaState>(new FakeAreaState("AreaSerbule"));
        services.AddSingleton<IReferenceDataService>(new FakeAreaReferenceData());
        services.AddSingleton<IMapCalibrationService>(new FakeCalibrationService());

        services.AddMithrilMapCalibrationCapture(settingsDir, assetCacheDir);

        using var provider = services.BuildServiceProvider(new ServiceProviderOptions
        {
            ValidateOnBuild = true,
            ValidateScopes = true,
        });

        provider.GetRequiredService<AutoCalibrationEngine>().Should().NotBeNull();

        var hotkeys = provider.GetServices<IHotkeyCommand>().ToList();
        hotkeys.Should().Contain(h => h.Id == "mapcalibration.capture");
        hotkeys.Should().Contain(h => h.Id == "mapcalibration.draw_bbox");

        // The GameConfig-wired gate must win over the engine's default gate
        // (last-registration-wins): a residual of 9.5 exceeds the configured 9.0.
        var gate = provider.GetRequiredService<Detection.ICalibrationConfidenceGate>();
        gate.Accept(new AreaCalibration(1, 0, 0, 0, 8, 9.5), 8, out _).Should().BeFalse();

        provider.GetRequiredService<AutoCalibrationTrigger>().Should().NotBeNull();
        provider.GetServices<Microsoft.Extensions.Hosting.IHostedService>()
            .Should().Contain(s => s is AutoCalibrationTrigger);
    }
}
