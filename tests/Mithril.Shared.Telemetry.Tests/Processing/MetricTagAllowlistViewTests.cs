using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics.Metrics;
using System.Linq;
using FluentAssertions;
using Microsoft.Extensions.Options;
using Mithril.Shared.Telemetry.Abstractions;
using Mithril.Shared.Telemetry.Catalog;
using Mithril.Shared.Telemetry.Processing;
using Mithril.Shared.Telemetry.Settings;
using OpenTelemetry;
using OpenTelemetry.Metrics;
using Xunit;

namespace Mithril.Shared.Telemetry.Tests.Processing;

/// <summary>
/// Behavioural contract for <see cref="MetricTagAllowlistView"/> — the
/// metrics-pipeline analogue of <see cref="AllowlistAndRedactionProcessor"/>.
///
/// The OTel SDK applies <see cref="MetricStreamConfiguration.TagKeys"/> by
/// retaining only listed keys in the aggregated metric points. We assert
/// that the catalog allowlist + user override semantics show up as the
/// expected key set on the exported metric points.
/// </summary>
public class MetricTagAllowlistViewTests
{
    private sealed class Provider(params TagDescriptor[] ds) : ITagDescriptorProvider
    {
        public IReadOnlyCollection<TagDescriptor> Describe() => ds;
    }

    private sealed class TestMonitor(TelemetrySettings s) : IOptionsMonitor<TelemetrySettings>
    {
        public TelemetrySettings CurrentValue => s;
        public TelemetrySettings Get(string? name) => s;
        public IDisposable OnChange(Action<TelemetrySettings, string?> listener) => new Sub();
        private sealed class Sub : IDisposable { public void Dispose() { } }
    }

    /// <summary>
    /// Minimal in-memory metric exporter that snapshots tag key/value pairs
    /// from every exported metric point. Avoids a dependency on the optional
    /// <c>OpenTelemetry.Exporter.InMemory</c> package — we only need to read
    /// the post-view tag set.
    /// </summary>
    private sealed class InMemoryMetricExporter : BaseExporter<Metric>
    {
        public List<KeyValuePair<string, object?>> Tags { get; } = new();

        public override ExportResult Export(in Batch<Metric> batch)
        {
            foreach (var metric in batch)
            {
                foreach (var point in metric.GetMetricPoints())
                {
                    foreach (var tag in point.Tags)
                    {
                        Tags.Add(tag);
                    }
                }
            }
            return ExportResult.Success;
        }
    }

    private static (MeterProvider provider, Meter meter, InMemoryMetricExporter exporter) BuildPipeline(
        string meterName,
        TagDescriptor[] catalog,
        ConcurrentDictionary<string, bool>? userOverrides = null)
    {
        var settings = new TelemetrySettings { TagExports = userOverrides ?? new() };
        var monitor = new TestMonitor(settings);
        var tagCatalog = new TagCatalog(new[] { new Provider(catalog) });

        var exporter = new InMemoryMetricExporter();
        var meter = new Meter(meterName);

        var providerBuilder = Sdk.CreateMeterProviderBuilder()
            .AddMeter(meterName)
            .AddReader(new PeriodicExportingMetricReader(exporter, exportIntervalMilliseconds: int.MaxValue));

        MetricTagAllowlistView.AddCatalogAllowlistView(providerBuilder, tagCatalog, monitor);

        return (providerBuilder.Build(), meter, exporter);
    }

    [Fact]
    public void Drops_metric_dimension_whose_key_is_unknown_to_the_catalog()
    {
        // Only "kept" is in the catalog; "unknown" should be filtered out.
        var meterName = $"Mithril.Test.{Guid.NewGuid():N}";
        var keptDesc = new TagDescriptor("kept", PiiClassification.Safe, "Mithril.Test", "");
        var (provider, meter, exporter) = BuildPipeline(meterName, new[] { keptDesc });
        using (provider)
        {
            var counter = meter.CreateCounter<long>("c");
            counter.Add(1,
                new KeyValuePair<string, object?>("kept", "v"),
                new KeyValuePair<string, object?>("unknown", "leak"));
            provider.ForceFlush();

            exporter.Tags.Should().Contain(kv => kv.Key == "kept" && (string?)kv.Value == "v");
            exporter.Tags.Should().NotContain(kv => kv.Key == "unknown",
                "metric dimensions whose key is absent from the TagCatalog must be dropped " +
                "fail-closed — same contract as the span scrubber");
        }
        meter.Dispose();
    }

    [Fact]
    public void Drops_sensitive_classified_dimension_by_default()
    {
        var meterName = $"Mithril.Test.{Guid.NewGuid():N}";
        var safe = new TagDescriptor("safe", PiiClassification.Safe, "Mithril.Test", "");
        var sensitive = new TagDescriptor("character.name", PiiClassification.Sensitive, "Mithril.Test", "");
        var (provider, meter, exporter) = BuildPipeline(meterName, new[] { safe, sensitive });
        using (provider)
        {
            var counter = meter.CreateCounter<long>("c");
            counter.Add(1,
                new KeyValuePair<string, object?>("safe", "v"),
                new KeyValuePair<string, object?>("character.name", "Thorgrim"));
            provider.ForceFlush();

            exporter.Tags.Should().Contain(kv => kv.Key == "safe");
            exporter.Tags.Should().NotContain(kv => kv.Key == "character.name",
                "Sensitive-classified dimensions default to NOT exported on metrics, same as on spans");
        }
        meter.Dispose();
    }

    [Fact]
    public void Keeps_user_enabled_sensitive_dimension_when_TagExports_overrides()
    {
        var meterName = $"Mithril.Test.{Guid.NewGuid():N}";
        var sensitive = new TagDescriptor("character.name", PiiClassification.Sensitive, "Mithril.Test", "");
        var overrides = new ConcurrentDictionary<string, bool>();
        overrides["character.name"] = true;

        var (provider, meter, exporter) = BuildPipeline(meterName, new[] { sensitive }, overrides);
        using (provider)
        {
            var counter = meter.CreateCounter<long>("c");
            counter.Add(1, new KeyValuePair<string, object?>("character.name", "Thorgrim"));
            provider.ForceFlush();

            exporter.Tags.Should().Contain(kv => kv.Key == "character.name",
                "user-promoted Sensitive dimensions flow through the view filter — " +
                "the chip-cloud toggle is honoured at metric-instrument-config time");
        }
        meter.Dispose();
    }

    [Fact]
    public void Drops_user_disabled_dimension_even_if_default_exported()
    {
        var meterName = $"Mithril.Test.{Guid.NewGuid():N}";
        var safe = new TagDescriptor("module.id", PiiClassification.Identifying, "Mithril.Test", "");
        var overrides = new ConcurrentDictionary<string, bool>();
        overrides["module.id"] = false;

        var (provider, meter, exporter) = BuildPipeline(meterName, new[] { safe }, overrides);
        using (provider)
        {
            var counter = meter.CreateCounter<long>("c");
            counter.Add(1, new KeyValuePair<string, object?>("module.id", "samwise"));
            provider.ForceFlush();

            exporter.Tags.Should().NotContain(kv => kv.Key == "module.id",
                "user-disabled dimensions are filtered out even if the catalog default would allow them");
        }
        meter.Dispose();
    }

    [Fact]
    public void Leaves_non_mithril_meters_unfiltered()
    {
        // The view callback only applies to Mithril.* meters; everything else
        // (auto-instrumentation HTTP / runtime / etc.) must pass through with
        // its full producer-emitted dimension set.
        var meterName = $"OtherVendor.Test.{Guid.NewGuid():N}";
        var (provider, meter, exporter) = BuildPipeline(meterName, Array.Empty<TagDescriptor>());
        using (provider)
        {
            var counter = meter.CreateCounter<long>("c");
            counter.Add(1, new KeyValuePair<string, object?>("any.vendor.tag", "v"));
            provider.ForceFlush();

            exporter.Tags.Should().Contain(kv => kv.Key == "any.vendor.tag",
                "auto-instrumentation meters are out of the catalog's scope; the view must not " +
                "swallow their dimensions");
        }
        meter.Dispose();
    }
}
