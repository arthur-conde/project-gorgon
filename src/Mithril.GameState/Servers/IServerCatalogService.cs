namespace Mithril.GameState.Servers;

/// <summary>
/// Reference-scope catalog of PG world servers, parsed once at attach from
/// Player.log's <c>Servers: [ … ]</c> startup line. Immutable for the
/// Mithril attach lifetime — PG re-emits the same catalog every launch and
/// the set is stable across PG patches, so the service exposes lookup +
/// enumeration only, not mutation or change notifications.
///
/// <para><b>The join target for #611.</b> <c>ConnectionEventParser</c>
/// observes <c>EVENT(Ok): connected, url=&lt;url&gt;</c> lines and looks up
/// the matching <see cref="ServerEntry"/> here to derive the
/// <c>GameSession.Server</c> name. <c>Get(url)</c>'s nullable return
/// reflects the only honest behaviour: when Mithril cold-starts mid-PG-
/// session, L0's seed strategy seeks past the preamble (the <c>Servers:</c>
/// line lives there) and the catalog stays empty for the attach lifetime
/// — a consumer trying to resolve <c>connected, url=…</c> in that mode
/// gets <c>null</c> rather than a fabricated default. The catalog
/// populates when Mithril attaches BEFORE PG launches, or when PG restarts
/// while Mithril is running (file truncation re-seeds L0 from byte 0).</para>
///
/// <para>Five servers are observed in current logs (s0=Arisetsu …
/// s4=Laeth). The shape is intentionally reference-data-like; an
/// alternative implementation could ship a bundled fallback in
/// <c>Mithril.Reference</c>. The log-derived path is the canonical input,
/// per #610's spec.</para>
/// </summary>
public interface IServerCatalogService
{
    /// <summary>
    /// Look up a server by its connection host (e.g.
    /// <c>"s4.projectgorgon.com"</c>). Case-insensitive — PG canonicalizes
    /// hostnames to lowercase but consumers' <c>connected, url=…</c>
    /// strings may have either case in principle. Returns <c>null</c> if
    /// no entry matches, including the empty-catalog case described on the
    /// interface summary.
    /// </summary>
    ServerEntry? Get(string url);

    /// <summary>
    /// All known server entries in PG's emission order (which happens to be
    /// highest-id first in current logs, but the order is not contractual).
    /// Empty until the parser has observed a <c>Servers:</c> line; once
    /// populated, stable for the attach lifetime.
    /// </summary>
    IReadOnlyCollection<ServerEntry> All { get; }
}
