using System;
using System.IO;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using Mithril.Shared.DependencyInjection;
using Mithril.Shared.Diagnostics.Telemetry;
using Mithril.Shared.Telemetry.Abstractions;
using Mithril.Shared.Telemetry.Hosting;
using Mithril.Shared.Telemetry.Settings;
using OpenTelemetry.Trace;
using Xunit;

namespace Mithril.Shared.Telemetry.Tests.Hosting;

public class TelemetryHostExtensionsTests
{
    [Fact]
    public void Disabled_settings_registers_no_TracerProvider()
    {
        var settings = new TelemetrySettings { EnableOtlpExport = false };

        using var host = Host.CreateDefaultBuilder()
            .ConfigureServices(services => services.AddMithrilOtlpExport(settings))
            .Build();

        host.Services.GetService<TracerProvider>().Should().BeNull(
            "AddMithrilOtlpExport must be a complete no-op when EnableOtlpExport is false (zero-overhead contract).");
    }

    [Fact]
    public void Enabled_settings_registers_TracerProvider()
    {
        var settings = new TelemetrySettings
        {
            EnableOtlpExport = true,
            // Unreachable port — the OTel exporter's connect failure is absorbed
            // by retry backoff; we are asserting DI wireup, not export success.
            Endpoint = "http://localhost:65535/",
            Protocol = OtlpProtocol.HttpProtobuf,
        };

        using var host = Host.CreateDefaultBuilder()
            .ConfigureServices(services =>
            {
                // Task 13 will register the settings instance as a singleton via
                // AddMithrilVersionedSettings<T>; mirror that here so the
                // IOptionsMonitor shim has a singleton source of truth.
                services.AddSingleton(settings);
                // TagCatalog ctor requires at least the canonical Mithril.Shared
                // provider so the union is non-empty and the conflict detector
                // has something to run against.
                services.AddSingleton<ITagDescriptorProvider, MithrilSharedTagDescriptors>();
                services.AddMithrilOtlpExport(settings);
            })
            .Build();

        host.Services.GetService<TracerProvider>().Should().NotBeNull(
            "Enabling export must wire the OpenTelemetry tracer provider so Mithril.* ActivitySources gain a listener.");
    }

    [Fact]
    public void TagExports_mutations_on_singleton_are_visible_via_OptionsMonitor()
    {
        var settings = new TelemetrySettings
        {
            EnableOtlpExport = true,
            Endpoint = "http://localhost:65535/",
        };

        using var host = Host.CreateDefaultBuilder()
            .ConfigureServices(services =>
            {
                services.AddSingleton(settings);
                services.AddSingleton<ITagDescriptorProvider, MithrilSharedTagDescriptors>();
                services.AddMithrilOtlpExport(settings);
            })
            .Build();

        var monitor = host.Services.GetRequiredService<IOptionsMonitor<TelemetrySettings>>();
        monitor.CurrentValue.TagExports.Should().BeEmpty();

        // In-place mutation on the singleton — exactly what the settings UI does
        // when the user toggles a tag-cloud chip.
        settings.TagExports["module.id"] = false;

        monitor.CurrentValue.TagExports.Should().ContainKey("module.id",
            "scrubber reads TagExports per span via CurrentValue; aliasing the IOptionsMonitor " +
            "snapshot away from the singleton would silently strand the user toggle until restart.");
    }

    [Fact]
    public void TagExports_mutations_are_visible_when_settings_resolved_via_AddMithrilVersionedSettings()
    {
        // Mirror the production Shell composition: settings come from a persisted
        // store via AddMithrilVersionedSettings<T>, and AddMithrilOtlpExport is
        // called with a settings snapshot resolved from a temporary provider for
        // the EnableOtlpExport gating decision. The IOptionsMonitor must resolve
        // the host's singleton, not the snapshot — otherwise UI mutations on the
        // host's TagExports dictionary silently strand until restart.
        var settingsDir = Path.Combine(Path.GetTempPath(), $"telemetry-test-{Guid.NewGuid():N}");
        Directory.CreateDirectory(settingsDir);
        try
        {
            var hb = Host.CreateApplicationBuilder();
            hb.Services.AddMithrilVersionedSettings<TelemetrySettings>(
                Path.Combine(settingsDir, "telemetry.json"),
                TelemetrySettingsJsonContext.Default.TelemetrySettings);
            hb.Services.AddSingleton<ITagDescriptorProvider, MithrilSharedTagDescriptors>();
            // Mirror Shell's BuildServiceProvider step exactly:
            TelemetrySettings snapshot;
            using (var tmp = hb.Services.BuildServiceProvider())
            {
                snapshot = tmp.GetRequiredService<TelemetrySettings>();
                snapshot.EnableOtlpExport = true;
                snapshot.Endpoint = "http://localhost:65535/";
            }
            hb.Services.AddMithrilOtlpExport(snapshot);
            using var host = hb.Build();

            var hostSingleton = host.Services.GetRequiredService<TelemetrySettings>();
            var monitor = host.Services.GetRequiredService<IOptionsMonitor<TelemetrySettings>>();
            // The host's singleton must be what the monitor returns — NOT the snapshot.
            monitor.CurrentValue.Should().BeSameAs(hostSingleton);
            // And mutations on the host singleton must show up immediately.
            hostSingleton.TagExports["module.id"] = false;
            monitor.CurrentValue.TagExports.Should().ContainKey("module.id");
        }
        finally
        {
            try { Directory.Delete(settingsDir, recursive: true); } catch { /* best-effort */ }
        }
    }
}
