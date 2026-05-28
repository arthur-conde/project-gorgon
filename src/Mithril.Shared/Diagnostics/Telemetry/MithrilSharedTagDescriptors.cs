using System.Collections.Generic;
using Mithril.Shared.Telemetry.Abstractions;

namespace Mithril.Shared.Diagnostics.Telemetry;

/// <summary>
/// Declares every tag key emitted on spans (<see cref="MithrilActivitySources"/>)
/// and metric instruments (<see cref="MithrilMeters"/>) from <c>Mithril.Shared</c>
/// and <c>Mithril.Shell</c>. Co-located with the source/meter statics so adding
/// a new tag is a one-file edit. Loaded into the telemetry
/// <c>TagCatalog</c> via DI during shell composition.
/// </summary>
/// <remarks>
/// Tag keys here are the live OTel names (lowercase dotted) as they appear on
/// the <c>Activity.SetTag(...)</c> and <c>Counter.Add(..., KeyValuePair)</c>
/// call sites — not the CamelCase property names the file exporter writes to
/// the JSONL. Cross-reference: <c>docs/perf-trace-schema.md</c>.
/// </remarks>
public sealed class MithrilSharedTagDescriptors : ITagDescriptorProvider
{
    private static readonly TagDescriptor[] Descriptors =
    {
        // ── Mithril.Wpf — frame / dispatcher / counter / gc / input_latency / binding_error spans ──
        new("interval_ms",      PiiClassification.Safe, "Mithril.Wpf", "Frame interval in milliseconds (frame span)."),
        new("stall",            PiiClassification.Safe, "Mithril.Wpf", "True when frame interval exceeded the 33 ms stall threshold."),
        new("op",               PiiClassification.Safe, "Mithril.Wpf", "In-flight dispatcher op label on stall (reserved; currently empty)."),
        new("attribution",      PiiClassification.Safe, "Mithril.Wpf", "Stall cause: dispatcher | non-dispatcher."),
        new("priority",         PiiClassification.Safe, "Mithril.Wpf", "DispatcherPriority of the op (Render, DataBind, Background, ...)."),
        new("wait_ms",          PiiClassification.Safe, "Mithril.Wpf", "Dispatcher op queued-wait time in milliseconds (currently 0 — no public API)."),
        new("depth",            PiiClassification.Safe, "Mithril.Wpf", "In-flight dispatcher op count when this op began executing."),
        new("kind",             PiiClassification.Safe, "Mithril.Wpf", "Input event kind for input_latency span: mouse | key."),
        new("working_set_mb",   PiiClassification.Safe, "Mithril.Wpf", "Process working set in megabytes (1 Hz counter span)."),
        new("gen0",             PiiClassification.Safe, "Mithril.Wpf", "Cumulative Gen-0 GC collection count."),
        new("gen1",             PiiClassification.Safe, "Mithril.Wpf", "Cumulative Gen-1 GC collection count."),
        new("gen2",             PiiClassification.Safe, "Mithril.Wpf", "Cumulative Gen-2 GC collection count."),
        new("threads",          PiiClassification.Safe, "Mithril.Wpf", "Process thread count snapshot."),
        new("handles",          PiiClassification.Safe, "Mithril.Wpf", "Process handle count snapshot."),
        new("queue_depth",      PiiClassification.Safe, "Mithril.Wpf", "Current in-flight dispatcher op count."),
        new("generation",       PiiClassification.Safe, "Mithril.Wpf", "GC generation observed (0 | 1 | 2) for gc span."),
        new("duration_ms",      PiiClassification.Safe, "Mithril.Wpf", "GC pause duration in milliseconds (currently 0 — polling has no start/stop ticks)."),
        new("message",          PiiClassification.Identifying, "Mithril.Wpf", "WPF data-binding error message text."),

        // ── Mithril.Shell.Modules — gate/view/discover/activate spans ──
        new("module.id",        PiiClassification.Identifying, "Mithril.Shell.Modules", "Module id (samwise, pippin, ...) — the module-activated / gate_open / view_resolve subject."),
        new("discovered_count", PiiClassification.Safe,         "Mithril.Shell.Modules", "Number of IMithrilModule implementations found on disk during module discovery."),

        // ── Mithril.Reference — ref_fetch span + mithril.reference.fetch_outcome counter ──
        new("file",             PiiClassification.Identifying, "Mithril.Reference", "Reference data file name (items, recipes, npcs, ...)."),
        new("cache_hit",        PiiClassification.Safe,         "Mithril.Reference", "True when reference fetch was served from on-disk cache."),
        new("outcome",          PiiClassification.Safe,         "Mithril.Reference", "Reference fetch outcome: cdn | cdn_failed | cache | cache_failed | bundled."),
        new("bytes",            PiiClassification.Safe,         "Mithril.Reference", "Reference fetch payload size in bytes."),
    };

    /// <inheritdoc />
    public IReadOnlyCollection<TagDescriptor> Describe() => Descriptors;
}
