namespace Mithril.GameState.Servers;

/// <summary>
/// One PG world server, parsed from the <c>Servers: [ { … }, … ]</c> JSON
/// array PG emits at startup. The catalog is the join target for
/// <c>ConnectionEventParser</c> (#611) — incoming <c>EVENT(Ok): connected,
/// url=…</c> lines look up the server by <see cref="Url"/> to resolve the
/// human-facing <see cref="Name"/> for <c>IGameSessionService.Server</c>.
///
/// <para>Five servers exist in the corpus today (<c>s0</c>=Arisetsu …
/// <c>s4</c>=Laeth); the set is stable across PG patches but not part of
/// any bundled reference data — the catalog is purely log-derived per #610
/// so a future server addition surfaces automatically the next time
/// Mithril observes the <c>Servers:</c> line.</para>
///
/// <para><see cref="Description"/> carries PG's marketing copy verbatim
/// (BBCode + JSON-escaped newlines included). It is preserved for the rare
/// debug surface; production consumers should display <see cref="Name"/>.</para>
/// </summary>
/// <param name="Id">PG server id (<c>s0</c>–<c>s4</c>). Stable across the
/// catalog's lifetime; safe to use as a dictionary key alongside
/// <see cref="Url"/>.</param>
/// <param name="Name">Human-facing world name (<c>Arisetsu</c>, <c>Dreva</c>,
/// <c>Strekios</c>, <c>Miraverre</c>, <c>Laeth</c>, …). The value populated
/// into <c>GameSession.Server</c> by #611.</param>
/// <param name="Url">Connection host (e.g. <c>s4.projectgorgon.com</c>). The
/// join key for <c>EVENT(Ok): connected, url=…</c> events.</param>
/// <param name="Port">TCP port the client connects to.</param>
/// <param name="Description">PG's marketing description for the world.
/// Verbatim — BBCode tags and embedded newlines preserved.</param>
public sealed record ServerEntry(
    string Id,
    string Name,
    string Url,
    int Port,
    string Description);
