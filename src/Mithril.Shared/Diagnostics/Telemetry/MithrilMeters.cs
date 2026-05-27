using System.Diagnostics.Metrics;

namespace Mithril.Shared.Diagnostics.Telemetry;

/// <summary>
/// Canonical <see cref="Meter"/> instances and instruments for Mithril.
/// Centralising instrument creation here keeps instrument names in one
/// place — the perf-recorder file exporter and any future exporter
/// (issue #815) reference these by name when mapping measurements back
/// to record kinds.
///
/// Instrument names follow OTel semantic-convention style: snake.case
/// namespaced under <c>mithril.&lt;subsystem&gt;.&lt;instrument&gt;</c>.
/// Tag/attribute names are lowercase dotted (<c>priority</c>,
/// <c>run_ms</c>, <c>module.id</c>, <c>cache_hit</c>, etc.).
/// </summary>
public static class MithrilMeters
{
    public const string Prefix = "Mithril.";

    /// <summary>WPF render + dispatcher + input + binding instruments.</summary>
    public static class Wpf
    {
        public static readonly Meter Meter = new("Mithril.Wpf");

        /// <summary>Per-frame interval in milliseconds. Tags: <c>stall</c> (bool), <c>op</c>, <c>attribution</c>.</summary>
        public static readonly Histogram<double> FrameIntervalMs =
            Meter.CreateHistogram<double>("mithril.wpf.frame.interval_ms", unit: "ms");

        /// <summary>Dispatcher operation wait time. Tags: <c>priority</c>, <c>depth</c>.</summary>
        public static readonly Histogram<double> DispatcherWaitMs =
            Meter.CreateHistogram<double>("mithril.wpf.dispatcher.wait_ms", unit: "ms");

        /// <summary>Dispatcher operation run time. Tags: <c>priority</c>, <c>depth</c>.</summary>
        public static readonly Histogram<double> DispatcherRunMs =
            Meter.CreateHistogram<double>("mithril.wpf.dispatcher.run_ms", unit: "ms");

        /// <summary>Input → first-paint latency in milliseconds. Tag: <c>kind</c> (mouse/key).</summary>
        public static readonly Histogram<double> InputLatencyMs =
            Meter.CreateHistogram<double>("mithril.wpf.input.latency_ms", unit: "ms");

        /// <summary>Binding errors emitted (throttled at the producer). No message tag — that would be unbounded cardinality; the message rides as a span tag on a <c>binding_error</c> Activity instead.</summary>
        public static readonly Counter<long> BindingErrors =
            Meter.CreateCounter<long>("mithril.wpf.binding_error");
    }

    /// <summary>Runtime + GC counters. Process-wide; observable gauges polled once per second.</summary>
    public static class Runtime
    {
        public static readonly Meter Meter = new("Mithril.Runtime");

        /// <summary>GC pause duration. Tag: <c>generation</c> (0/1/2). The current producer emits 0.0 — kept as a histogram so a future real GC timer fits without schema break.</summary>
        public static readonly Histogram<double> GcDurationMs =
            Meter.CreateHistogram<double>("mithril.runtime.gc.duration_ms", unit: "ms");
    }

    // Arda counters live in Arda.Abstractions.Diagnostics.ArdaMeters (same reason as
    // ArdaActivitySources — Arda can't depend on Mithril.Shared). Listener-side meter
    // dispatch is purely string-based on the instrument name.

    /// <summary>Reference-data fetch outcomes (PR B).</summary>
    public static class Reference
    {
        public static readonly Meter Meter = new("Mithril.Reference");

        /// <summary>Fetch outcome counter. Tag: <c>outcome</c> ∈ {cdn, cache, bundled}, <c>file</c>.</summary>
        public static readonly Counter<long> FetchOutcome =
            Meter.CreateCounter<long>("mithril.reference.fetch_outcome");
    }

    /// <summary>GameState services event counters (PR B). Per-service counters created on demand.</summary>
    public static class GameState
    {
        public static readonly Meter Meter = new("Mithril.GameState");
    }
}
