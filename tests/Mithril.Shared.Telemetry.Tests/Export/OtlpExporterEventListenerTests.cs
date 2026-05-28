using System.Diagnostics.Tracing;
using FluentAssertions;
using Mithril.Shared.Telemetry.Export;
using Xunit;

namespace Mithril.Shared.Telemetry.Tests.Export;

/// <summary>
/// Contract for mithril#834: the listener subscribes to the OTel SDK's
/// internal exporter <see cref="EventSource"/> and routes Warning+ events
/// into <see cref="ExporterHealthMonitor.RecordFailure(string)"/>. Success
/// is synthesised by an absence-of-failure tick (driven directly by tests
/// via the internal <c>TickSuccessForTests</c> hook).
/// </summary>
public class OtlpExporterEventListenerTests
{
    /// <summary>
    /// In-process stand-in for the OTel exporter's internal EventSource.
    /// Constructed with the same name (<see cref="OtlpExporterEventListener.OtlpExporterEventSourceName"/>)
    /// so the listener's <c>OnEventSourceCreated</c> filter matches. Subscribers
    /// to one EventSource instance under that name see events from any other
    /// instance under the same name in the same process — the listener's filter
    /// is name-based.
    /// </summary>
    private sealed class FakeOtlpEventSource() : EventSource(OtlpExporterEventListener.OtlpExporterEventSourceName)
    {
        [Event(1, Level = EventLevel.Error)]
        public void ExportFailed(string reason) => WriteEvent(1, reason);

        [Event(2, Level = EventLevel.Warning)]
        public void TransientHttpError(string status) => WriteEvent(2, status);

        [Event(3, Level = EventLevel.Informational)]
        public void HousekeepingInfo(string note) => WriteEvent(3, note);
    }

    [Fact]
    public void Error_level_event_from_otlp_source_records_failure()
    {
        using var health = new ExporterHealthMonitor();
        using var src = new FakeOtlpEventSource();
        // Build the listener AFTER the source so OnEventSourceCreated fires for
        // an already-existing source — this exercises the initialization race
        // path (queued source enabled via Initialize, not OnEventSourceCreated).
        using var listener = new OtlpExporterEventListener(health, TimeSpan.FromSeconds(30), startTimer: false);

        src.ExportFailed("Connection refused");

        health.Current.LastError.Should().NotBeNull();
        health.Current.LastError.Should().Contain("ExportFailed");
        health.Current.LastError.Should().Contain("Connection refused");
        health.Current.LastFailureUtc.Should().NotBeNull();
    }

    [Fact]
    public void Warning_level_event_from_otlp_source_records_failure()
    {
        using var health = new ExporterHealthMonitor();
        using var src = new FakeOtlpEventSource();
        using var listener = new OtlpExporterEventListener(health, TimeSpan.FromSeconds(30), startTimer: false);

        src.TransientHttpError("503 ServiceUnavailable");

        health.Current.LastError.Should().NotBeNull();
        health.Current.LastError.Should().Contain("503 ServiceUnavailable");
    }

    [Fact]
    public void Informational_level_event_does_not_record_failure()
    {
        using var health = new ExporterHealthMonitor();
        using var src = new FakeOtlpEventSource();
        using var listener = new OtlpExporterEventListener(health, TimeSpan.FromSeconds(30), startTimer: false);

        src.HousekeepingInfo("opened persistent retry directory");

        // Informational events live below the Warning threshold the listener
        // monitors — they must not turn the status line red.
        health.Current.LastError.Should().BeNull();
        health.Current.LastFailureUtc.Should().BeNull();
    }

    [Fact]
    public void Tick_with_no_prior_failure_records_success()
    {
        using var health = new ExporterHealthMonitor();
        using var listener = new OtlpExporterEventListener(health, TimeSpan.FromSeconds(30), startTimer: false);

        var recorded = listener.TickSuccessForTests();

        recorded.Should().BeTrue();
        health.Current.LastSuccessUtc.Should().NotBeNull();
    }

    [Fact]
    public void Tick_immediately_after_failure_suppresses_success()
    {
        using var health = new ExporterHealthMonitor();
        using var src = new FakeOtlpEventSource();
        // Long window so a within-window tick is suppressed.
        using var listener = new OtlpExporterEventListener(health, TimeSpan.FromHours(1), startTimer: false);

        src.ExportFailed("DNS resolution failed");
        var recorded = listener.TickSuccessForTests();

        recorded.Should().BeFalse(
            "absence-of-failure must respect the window — a tick immediately after a recorded " +
            "failure should not flip the status line back to green.");
        health.Current.LastError.Should().Be(health.Current.LastError);
        // Verify the failure remained the latest signal.
        health.Current.LastError.Should().Contain("DNS resolution failed");
    }

    [Fact]
    public void Tick_after_window_elapses_following_failure_records_success()
    {
        using var health = new ExporterHealthMonitor();
        using var src = new FakeOtlpEventSource();
        // Zero-length window: any tick is "after the window" for the prior failure.
        using var listener = new OtlpExporterEventListener(health, TimeSpan.Zero, startTimer: false);

        src.ExportFailed("transient");
        var recorded = listener.TickSuccessForTests();

        recorded.Should().BeTrue();
        // Both timestamps are populated — failure history is preserved alongside the new success.
        health.Current.LastFailureUtc.Should().NotBeNull();
        health.Current.LastSuccessUtc.Should().NotBeNull();
    }

    [Fact]
    public void Events_from_unrelated_event_sources_are_ignored()
    {
        using var health = new ExporterHealthMonitor();
        using var unrelated = new UnrelatedErrorEventSource();
        using var listener = new OtlpExporterEventListener(health, TimeSpan.FromSeconds(30), startTimer: false);

        unrelated.SomethingBroke("unrelated subsystem");

        health.Current.LastError.Should().BeNull(
            "the listener must filter by EventSource name — only failures from the OTel exporter " +
            "source itself should drive the status line.");
    }

    private sealed class UnrelatedErrorEventSource() : EventSource("Mithril-Unrelated-Test-Source")
    {
        [Event(1, Level = EventLevel.Error)]
        public void SomethingBroke(string detail) => WriteEvent(1, detail);
    }

    [Fact]
    public void Listener_self_subscribes_when_constructed_after_source_already_exists()
    {
        // Exercises the "source exists before listener" path: the listener's
        // base ctor sees the source in its initial walk, defers EnableEvents
        // until Initialize sets _health, then enables. The first event after
        // that point must be recorded.
        using var src = new FakeOtlpEventSource();
        using var health = new ExporterHealthMonitor();
        using var listener = new OtlpExporterEventListener(health, TimeSpan.FromSeconds(30), startTimer: false);

        src.ExportFailed("post-construction failure");
        health.Current.LastError.Should().Contain("post-construction failure");
    }

    [Fact]
    public void Listener_picks_up_source_constructed_after_listener()
    {
        // Exercises the "listener exists before source" path: OnEventSourceCreated
        // fires after Initialize, so the source is enabled inline.
        using var health = new ExporterHealthMonitor();
        using var listener = new OtlpExporterEventListener(health, TimeSpan.FromSeconds(30), startTimer: false);
        using var src = new FakeOtlpEventSource();

        src.ExportFailed("late-bound source failure");
        health.Current.LastError.Should().Contain("late-bound source failure");
    }
}
