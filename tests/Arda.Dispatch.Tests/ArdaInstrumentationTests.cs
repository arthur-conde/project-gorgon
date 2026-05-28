using System.Diagnostics;
using System.Diagnostics.Metrics;
using Arda.Abstractions.Diagnostics;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Arda.Dispatch.Tests;

/// <summary>
/// Producer-side tests for the PR-B Arda instrumentation. Asserts that
/// <see cref="DomainEventBus"/> emits the expected spans and counters when
/// a listener is attached. Schema parity (Activity → JSON-lines) is covered
/// by <c>PerfTracerTests</c> in <c>Mithril.Shared.Tests</c>; this file locks
/// the producer-side shape so PR B can't drift away from what the exporter
/// dispatch arms expect.
/// </summary>
[Collection(TelemetryTestCollection.Name)]
public class ArdaInstrumentationTests
{
    private readonly record struct CompositionEvent(int Value);

    private sealed record CapturedActivity(string Source, string Op, Dictionary<string, object?> Tags);

    private static (ActivityListener listener, List<CapturedActivity> log) CaptureActivities()
    {
        var log = new List<CapturedActivity>();
        var listener = new ActivityListener
        {
            ShouldListenTo = s => s.Name.StartsWith("Mithril.Arda.", StringComparison.Ordinal),
            Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllData,
            ActivityStopped = a => log.Add(new CapturedActivity(
                a.Source.Name, a.OperationName,
                a.TagObjects.ToDictionary(t => t.Key, t => t.Value))),
        };
        ActivitySource.AddActivityListener(listener);
        return (listener, log);
    }

    private sealed record CapturedMeasurement(string Instrument, long Value, Dictionary<string, object?> Tags);

    private static (MeterListener listener, List<CapturedMeasurement> log) CaptureMeasurements()
    {
        var log = new List<CapturedMeasurement>();
        var listener = new MeterListener
        {
            InstrumentPublished = (inst, l) =>
            {
                if (inst.Meter.Name == "Mithril.Arda") l.EnableMeasurementEvents(inst);
            },
        };
        listener.SetMeasurementEventCallback<long>((inst, val, tags, _) =>
            log.Add(new CapturedMeasurement(
                inst.Name, val,
                tags.ToArray().ToDictionary(t => t.Key, t => t.Value))));
        listener.Start();
        return (listener, log);
    }

    [Fact]
    public void DomainEventBus_Publish_increments_domain_event_published_counter()
    {
        var (mlistener, mlog) = CaptureMeasurements();
        try
        {
            var bus = new DomainEventBus(NullLogger<DomainEventBus>.Instance);
            bus.Publish(new CompositionEvent(42));
            bus.Publish(new CompositionEvent(7));

            mlog.Where(m => m.Instrument == "mithril.arda.domain_event.published")
                .Should().HaveCount(2);
            mlog.First(m => m.Instrument == "mithril.arda.domain_event.published")
                .Tags["event.type"].Should().Be(nameof(CompositionEvent));
        }
        finally { mlistener.Dispose(); }
    }

    [Fact]
    public void DomainEventBus_emits_compose_span_per_subscriber_when_listener_attached()
    {
        var (alistener, alog) = CaptureActivities();
        try
        {
            var bus = new DomainEventBus(NullLogger<DomainEventBus>.Instance);
            var subscriber = new FakeComposer();
            bus.Subscribe<CompositionEvent>(subscriber.OnEvent);

            bus.Publish(new CompositionEvent(1));

            alog.Should().ContainSingle(a => a.Source == "Mithril.Arda.Composition" && a.Op.StartsWith("compose."));
            var compose = alog.Single(a => a.Op.StartsWith("compose."));
            compose.Op.Should().Be("compose.FakeComposer");
            compose.Tags["event"].Should().Be(nameof(CompositionEvent));
        }
        finally { alistener.Dispose(); }
    }

    [Fact]
    public void Arda_meters_carry_source_tag_for_lines_and_unmatched()
    {
        var (mlistener, mlog) = CaptureMeasurements();
        try
        {
            ArdaMeters.LinesParsed.Add(1, new KeyValuePair<string, object?>("source", "player"));
            ArdaMeters.VerbUnmatched.Add(1, new KeyValuePair<string, object?>("source", "chat"));
            ArdaMeters.GrammarBreak.Add(1,
                new KeyValuePair<string, object?>("source", "player"),
                new KeyValuePair<string, object?>("verb", "ProcessAddItem"));

            mlog.Should().HaveCountGreaterOrEqualTo(3);
            mlog.Single(m => m.Instrument == "mithril.arda.lines_parsed").Tags["source"].Should().Be("player");
            mlog.Single(m => m.Instrument == "mithril.arda.verb.unmatched").Tags["source"].Should().Be("chat");
            var gb = mlog.Single(m => m.Instrument == "mithril.arda.grammar_break");
            gb.Tags["source"].Should().Be("player");
            gb.Tags["verb"].Should().Be("ProcessAddItem");
        }
        finally { mlistener.Dispose(); }
    }

    private sealed class FakeComposer
    {
        public void OnEvent(CompositionEvent _) { }
    }
}
