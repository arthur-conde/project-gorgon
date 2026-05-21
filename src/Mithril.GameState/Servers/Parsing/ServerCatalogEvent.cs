using Mithril.Shared.Logging;

namespace Mithril.GameState.Servers.Parsing;

/// <summary>
/// The parsed <c>Servers: [ … ]</c> startup line. Carries the full catalog
/// — PG always emits all servers in one line, so each event represents a
/// complete snapshot rather than an incremental delta. <see cref="Entries"/>
/// is ordered exactly as PG emitted it (which happens to be highest-id
/// first in current logs, but the parser does not depend on that).
/// </summary>
public sealed record ServerCatalogEvent(DateTime Timestamp, IReadOnlyList<ServerEntry> Entries)
    : LogEvent(Timestamp);
