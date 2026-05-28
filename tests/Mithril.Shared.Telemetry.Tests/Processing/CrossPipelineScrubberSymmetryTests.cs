using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Diagnostics.Metrics;
using System.Linq;
using FluentAssertions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Mithril.Shared.Telemetry.Abstractions;
using Mithril.Shared.Telemetry.Catalog;
using Mithril.Shared.Telemetry.Processing;
using Mithril.Shared.Telemetry.Settings;
using OpenTelemetry;
using OpenTelemetry.Logs;
using OpenTelemetry.Metrics;
using OpenTelemetry.Trace;
using Xunit;

namespace Mithril.Shared.Telemetry.Tests.Processing;

/// <summary>
/// Cross-pipeline parity check for mithril#841: a single
/// <c>Sensitive</c>-classified tag emitted on (a) a span, (b) a metric
/// dimension, and (c) a log attribute is dropped consistently by every
/// surface's scrubber. This is the load-bearing test that the user's
/// chip-cloud promise — "Sensitive = doesn't leave the machine by default" —
/// holds uniformly.
///
/// Mirror test for the safe case is implied — every per-surface test class
/// covers the keep-when-Safe path independently.
/// </summary>
public class CrossPipelineScrubberSymmetryTests
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

    private sealed class CaptureLogProcessor : BaseProcessor<LogRecord>
    {
        public List<List<KeyValuePair<string, object?>>?> AttrSnapshots { get; } = new();
        public override void OnEnd(LogRecord data)
            => AttrSnapshots.Add(data.Attributes is null ? null : data.Attributes.ToList());
    }

    private sealed class SymmetryMetricExporter : BaseExporter<Metric>
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

    [Fact]
    public void Sensitive_tag_is_dropped_on_spans_metrics_and_logs_alike()
    {
        const string sensitiveKey = "character.name";
        const string sensitiveValue = "Thorgrim";
        var sourceName = $"Mithril.Symmetry.{Guid.NewGuid():N}";

        var sensitive = new TagDescriptor(sensitiveKey, PiiClassification.Sensitive, "Mithril.Symmetry", "");
        var settings = new TelemetrySettings();
        var monitor = new TestMonitor(settings);
        var catalog = new TagCatalog(new[] { new Provider(sensitive) });
        var redactor = new ValueRedactor(() => null, @"C:\Users\u", @"C:\Users\u\AppData\Local");
        var observer = new NewlySeenTagsObserver();

        // ── Spans ────────────────────────────────────────────────────────────
        var spanProcessor = new AllowlistAndRedactionProcessor(catalog, monitor, redactor, observer);
        using var src = new ActivitySource(sourceName);
        using var spanListener = new ActivityListener
        {
            ShouldListenTo = s => s.Name == sourceName,
            Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllData,
            ActivityStopped = spanProcessor.OnEnd,
        };
        ActivitySource.AddActivityListener(spanListener);
        using (var act = src.StartActivity("op")!)
        {
            act.SetTag(sensitiveKey, sensitiveValue);
        }
        // Activity is stopped on dispose; the listener's ActivityStopped callback
        // ran the scrubber. Re-fetch the activity to inspect.
        using var act2 = src.StartActivity("op2")!;
        act2.SetTag(sensitiveKey, sensitiveValue);
        act2.Stop();
        act2.GetTagItem(sensitiveKey).Should().BeNull(
            "spans drop Sensitive-classified tags by default");

        // ── Metrics ──────────────────────────────────────────────────────────
        var meterName = $"Mithril.Symmetry.{Guid.NewGuid():N}";
        using var meter = new Meter(meterName);
        var metricExporter = new SymmetryMetricExporter();
        var metricProviderBuilder = Sdk.CreateMeterProviderBuilder()
            .AddMeter(meterName)
            .AddReader(new PeriodicExportingMetricReader(metricExporter, exportIntervalMilliseconds: int.MaxValue));
        MetricTagAllowlistView.AddCatalogAllowlistView(metricProviderBuilder, catalog, monitor);
        using (var metricProvider = metricProviderBuilder.Build())
        {
            var counter = meter.CreateCounter<long>("c");
            counter.Add(1, new KeyValuePair<string, object?>(sensitiveKey, sensitiveValue));
            metricProvider.ForceFlush();
        }
        metricExporter.Tags.Should().NotContain(kv => kv.Key == sensitiveKey,
            "metrics drop Sensitive-classified dimensions by default — same contract");

        // ── Logs ─────────────────────────────────────────────────────────────
        var logProcessor = new LogScrubbingProcessor(catalog, monitor, redactor, observer);
        var capture = new CaptureLogProcessor();
        using (var factory = LoggerFactory.Create(b =>
        {
            b.AddOpenTelemetry(o =>
            {
                o.IncludeFormattedMessage = true;
                o.ParseStateValues = true;
                o.AddProcessor(logProcessor);
                o.AddProcessor(capture);
            });
        }))
        {
            // MEL placeholder PascalCase mirrors the message-template convention;
            // the catalog entry above uses the same key.
            factory.CreateLogger("sym").LogInformation("event {character.name}", sensitiveValue);
        }
        capture.AttrSnapshots.Should().ContainSingle();
        capture.AttrSnapshots[0].Should().NotContain(kv => kv.Key == sensitiveKey,
            "logs drop Sensitive-classified attributes by default — same contract as spans / metrics");
    }
}
