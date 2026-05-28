using System.Diagnostics;

namespace Arda.Abstractions.Diagnostics;

/// <summary>
/// Canonical <see cref="ActivitySource"/> instances for the Arda pipeline,
/// keyed by layer. Names follow the <c>Mithril.Arda.&lt;layer&gt;</c> convention
/// so listeners filtering on the <c>"Mithril."</c> prefix capture both the
/// cross-cutting Mithril sources (defined in
/// <c>Mithril.Shared.Diagnostics.Telemetry.MithrilActivitySources</c>) and
/// these Arda-internal sources uniformly.
///
/// Arda projects can't depend on <c>Mithril.Shared</c> (the layering is the
/// other way), so the catalog lives here in <c>Arda.Abstractions</c> instead —
/// the foundational project both Ingest and Dispatch already reference.
/// </summary>
public static class ArdaActivitySources
{
    /// <summary>Arda L0/L1 — log tailing + line classification (batch boundaries).</summary>
    public static readonly ActivitySource Ingest = new("Mithril.Arda.Ingest");

    /// <summary>Arda L2 — verb extraction + dispatch table.</summary>
    public static readonly ActivitySource Dispatch = new("Mithril.Arda.Dispatch");

    /// <summary>Arda L3 — Player.log frame handlers.</summary>
    public static readonly ActivitySource Player = new("Mithril.Arda.Player");

    /// <summary>Arda L3 — ChatLogs frame handlers.</summary>
    public static readonly ActivitySource Chat = new("Mithril.Arda.Chat");

    /// <summary>Arda L4 — cross-source composers (inventory, session, progression).</summary>
    public static readonly ActivitySource Composition = new("Mithril.Arda.Composition");
}
