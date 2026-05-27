using System.Diagnostics.Metrics;

namespace Arda.Abstractions.Diagnostics;

/// <summary>
/// Canonical <see cref="Meter"/> and counters for the Arda pipeline. Names
/// follow the <c>mithril.arda.&lt;name&gt;</c> OTel semantic-convention style;
/// listeners filtering on the <c>"Mithril."</c> Meter-name prefix capture
/// these alongside the cross-cutting <c>Mithril.*</c> meters defined in
/// <c>Mithril.Shared.Diagnostics.Telemetry.MithrilMeters</c>.
/// </summary>
public static class ArdaMeters
{
    public static readonly Meter Meter = new("Mithril.Arda");

    /// <summary>Lines parsed. Tag: <c>source</c> (player | chat).</summary>
    public static readonly Counter<long> LinesParsed =
        Meter.CreateCounter<long>("mithril.arda.lines_parsed");

    /// <summary>Verb extraction produced no match — line had no recognisable opening token. Tag: <c>source</c>.</summary>
    public static readonly Counter<long> VerbUnmatched =
        Meter.CreateCounter<long>("mithril.arda.verb.unmatched");

    /// <summary>Dispatch table found no handlers registered for the verb. Tag: <c>verb</c>, <c>source</c>.</summary>
    public static readonly Counter<long> VerbUnhandled =
        Meter.CreateCounter<long>("mithril.arda.verb.unhandled");

    /// <summary>Frame handler refused the input (grammar break). Tag: <c>verb</c>, <c>source</c>.</summary>
    public static readonly Counter<long> GrammarBreak =
        Meter.CreateCounter<long>("mithril.arda.grammar_break");

    /// <summary>Domain event published through <c>IDomainEventBus</c>. Tag: <c>event.type</c> (the event struct's name).</summary>
    public static readonly Counter<long> DomainEventPublished =
        Meter.CreateCounter<long>("mithril.arda.domain_event.published");
}
