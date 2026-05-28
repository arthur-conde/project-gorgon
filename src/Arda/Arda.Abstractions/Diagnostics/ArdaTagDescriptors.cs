using System.Collections.Generic;
using Mithril.Shared.Telemetry.Abstractions;

namespace Arda.Abstractions.Diagnostics;

/// <summary>
/// Tag descriptors for spans (<see cref="ArdaActivitySources"/>) and metric
/// instruments (<see cref="ArdaMeters"/>) emitted from the Arda pipeline.
/// Co-located with the source/meter statics so adding a new tag is a one-file
/// edit. Loaded into the telemetry <c>TagCatalog</c> via DI during shell
/// composition. See <c>docs/perf-trace-schema.md</c> for the JSONL-side
/// projection (which uses CamelCase property names).
/// </summary>
public sealed class ArdaTagDescriptors : ITagDescriptorProvider
{
    private static readonly TagDescriptor[] Descriptors =
    {
        // ── Mithril.Arda.Ingest — arda_batch span + mithril.arda.lines_parsed/verb.unmatched counters ──
        // `source` is overloaded: BatchProcessor sets it to the tailed log path
        // (Sensitive — %LocalAppData%\Low\Elder Game\... contains the Windows
        // account name); WorldDriver and the per-line counters set it to the
        // safe source-family enum value (player | chat). We declare it once at
        // the worst-case classification so the ValueRedactor strips the path
        // form even if a user opts the key back on.
        new("source",            PiiClassification.Sensitive, "Mithril.Arda.Ingest", "Source identifier — either the tailed log file path (sensitive: contains user account) or the source-family enum (player | chat)."),
        new("line_count",        PiiClassification.Safe,      "Mithril.Arda.Ingest", "Raw lines processed (per batch in BatchProcessor; cumulative in WorldDriver)."),
        new("classified_count",  PiiClassification.Safe,      "Mithril.Arda.Ingest", "Lines that survived classification and were forwarded to L2 dispatch."),

        // ── Mithril.Arda.Dispatch — world_driver / arda_dispatch spans + verb counters ──
        new("source.family",     PiiClassification.Safe, "Mithril.Arda.Dispatch", "Source family the driver is processing: player | chat."),
        new("halted",            PiiClassification.Safe, "Mithril.Arda.Dispatch", "True if the driver loop stopped early via grammar break; false on normal exhaustion or cancellation."),
        new("verb",              PiiClassification.Safe, "Mithril.Arda.Dispatch", "Verb token extracted from the line (e.g. ProcessAddItem)."),
        new("handler.count",     PiiClassification.Safe, "Mithril.Arda.Dispatch", "Number of frame handlers registered for the dispatched verb."),

        // ── Mithril.Arda.Composition — compose.* spans + domain_event.published counter ──
        // `event` is the span tag (typeof(T).Name on the per-subscriber span);
        // `event.type` is the counter tag (typeof(T).Name on the publish-side
        // counter). Different keys — both declared.
        new("event",             PiiClassification.Safe, "Mithril.Arda.Composition", "Domain event struct name on the per-subscriber compose span."),
        new("event.type",        PiiClassification.Safe, "Mithril.Arda.Composition", "Domain event struct name on the publish-side counter."),
    };

    /// <inheritdoc />
    public IReadOnlyCollection<TagDescriptor> Describe() => Descriptors;
}
