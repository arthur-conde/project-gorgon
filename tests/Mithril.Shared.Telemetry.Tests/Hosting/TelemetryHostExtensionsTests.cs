using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
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
}
