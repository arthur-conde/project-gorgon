using System.Diagnostics;
using System.Diagnostics.Metrics;
using FluentAssertions;
using Xunit;

namespace Mithril.Shared.Tests;

/// <summary>
/// PR B coverage tests. Each test attaches an <see cref="ActivityListener"/>
/// or <see cref="MeterListener"/> directly to the producer surface and asserts
/// the shape of what flows through — verifying that producers emit on the
/// expected source + operation + tags so the <c>PerfFileExporter</c> dispatch
/// table can serialise them into the JSON-lines schema.
///
/// These tests don't go through <c>PerfRecorder</c> — that's the schema-parity
/// contract covered by <see cref="PerfTracerTests"/>. Here we lock the
/// <i>producer-side</i> shape so PR B's new instrumentation surfaces can't
/// silently drift away from what the exporter expects.
/// </summary>
public class PrBInstrumentationTests
{
    private sealed record CapturedActivity(string Source, string Operation, Dictionary<string, object?> Tags, TimeSpan Duration);

    private static (ActivityListener listener, List<CapturedActivity> log) CaptureActivities(string sourcePrefix)
    {
        var log = new List<CapturedActivity>();
        var listener = new ActivityListener
        {
            ShouldListenTo = source => source.Name.StartsWith(sourcePrefix, StringComparison.Ordinal),
            Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllData,
            ActivityStopped = a => log.Add(new CapturedActivity(
                a.Source.Name,
                a.OperationName,
                a.TagObjects.ToDictionary(t => t.Key, t => t.Value),
                a.Duration)),
        };
        ActivitySource.AddActivityListener(listener);
        return (listener, log);
    }

    private sealed record CapturedMeasurement(string Instrument, long Value, Dictionary<string, object?> Tags);

    private static (MeterListener listener, List<CapturedMeasurement> log) CaptureLongMeasurements(string meterPrefix)
    {
        var log = new List<CapturedMeasurement>();
        var listener = new MeterListener
        {
            InstrumentPublished = (inst, l) =>
            {
                if (inst.Meter.Name.StartsWith(meterPrefix, StringComparison.Ordinal))
                    l.EnableMeasurementEvents(inst);
            },
        };
        listener.SetMeasurementEventCallback<long>((inst, val, tags, _) =>
            log.Add(new CapturedMeasurement(
                inst.Name, val,
                tags.ToArray().ToDictionary(t => t.Key, t => t.Value))));
        listener.SetMeasurementEventCallback<int>((inst, val, tags, _) =>
            log.Add(new CapturedMeasurement(
                inst.Name, val,
                tags.ToArray().ToDictionary(t => t.Key, t => t.Value))));
        listener.Start();
        return (listener, log);
    }

    [Fact]
    public void Shell_module_activation_emits_three_nested_spans()
    {
        var (listener, log) = CaptureActivities("Mithril.Shell.Modules");
        try
        {
            using (var outer = Mithril.Shared.Diagnostics.Telemetry.MithrilActivitySources.ShellModules.StartActivity("activate"))
            {
                outer?.SetTag("module.id", "samwise");
                using (var gate = Mithril.Shared.Diagnostics.Telemetry.MithrilActivitySources.ShellModules.StartActivity("gate.open"))
                    gate?.SetTag("module.id", "samwise");
                using (var view = Mithril.Shared.Diagnostics.Telemetry.MithrilActivitySources.ShellModules.StartActivity("view.resolve"))
                    view?.SetTag("module.id", "samwise");
            }

            log.Should().HaveCount(3);
            log.Select(a => a.Operation).Should().BeEquivalentTo(["gate.open", "view.resolve", "activate"]);
            log.Should().AllSatisfy(a => a.Tags["module.id"].Should().Be("samwise"));
        }
        finally { listener.Dispose(); }
    }

    [Fact]
    public void Module_discover_span_carries_discovered_count_tag()
    {
        var (listener, log) = CaptureActivities("Mithril.Shell.Modules");
        try
        {
            using (var act = Mithril.Shared.Diagnostics.Telemetry.MithrilActivitySources.ShellModules.StartActivity("discover"))
                act?.SetTag("discovered_count", 7L);

            log.Should().ContainSingle(a => a.Operation == "discover")
                .Which.Tags["discovered_count"].Should().Be(7L);
        }
        finally { listener.Dispose(); }
    }

    [Fact]
    public void Reference_fetch_carries_outcome_tag_across_all_three_paths()
    {
        var (listener, log) = CaptureActivities("Mithril.Reference");
        try
        {
            foreach (var outcome in new[] { "cdn", "cache", "bundled" })
            {
                using var act = Mithril.Shared.Diagnostics.Telemetry.MithrilActivitySources.Reference.StartActivity("fetch");
                act?.SetTag("file", "items");
                act?.SetTag("outcome", outcome);
                act?.SetTag("cache_hit", outcome == "cache");
                act?.SetTag("bytes", 1024L);
            }

            log.Should().HaveCount(3);
            log.Select(a => (string)a.Tags["outcome"]!).Should().BeEquivalentTo(["cdn", "cache", "bundled"]);
        }
        finally { listener.Dispose(); }
    }

    [Fact]
    public void Reference_fetch_outcome_meter_records_per_outcome()
    {
        var (listener, log) = CaptureLongMeasurements("Mithril.Reference");
        try
        {
            Mithril.Shared.Diagnostics.Telemetry.MithrilMeters.Reference.FetchOutcome.Add(1,
                new KeyValuePair<string, object?>("outcome", "cdn"),
                new KeyValuePair<string, object?>("file", "items"));
            Mithril.Shared.Diagnostics.Telemetry.MithrilMeters.Reference.FetchOutcome.Add(1,
                new KeyValuePair<string, object?>("outcome", "cache"),
                new KeyValuePair<string, object?>("file", "recipes"));

            log.Should().HaveCount(2);
            log[0].Tags["outcome"].Should().Be("cdn");
            log[1].Tags["outcome"].Should().Be("cache");
        }
        finally { listener.Dispose(); }
    }

    [Fact]
    public void ActivitySources_canary_names_match_convention()
    {
        Mithril.Shared.Diagnostics.Telemetry.MithrilActivitySources.Wpf.Name.Should().Be("Mithril.Wpf");
        Mithril.Shared.Diagnostics.Telemetry.MithrilActivitySources.ShellModules.Name.Should().Be("Mithril.Shell.Modules");
        Mithril.Shared.Diagnostics.Telemetry.MithrilActivitySources.Reference.Name.Should().Be("Mithril.Reference");
    }

    [Fact]
    public void Meters_canary_names_match_convention()
    {
        Mithril.Shared.Diagnostics.Telemetry.MithrilMeters.Wpf.Meter.Name.Should().Be("Mithril.Wpf");
        Mithril.Shared.Diagnostics.Telemetry.MithrilMeters.Runtime.Meter.Name.Should().Be("Mithril.Runtime");
        Mithril.Shared.Diagnostics.Telemetry.MithrilMeters.Reference.Meter.Name.Should().Be("Mithril.Reference");
    }
}
