namespace Mithril.Shared.Logging;

/// <summary>
/// Discriminator for <see cref="SystemSignalLogLine"/>. Enumerates the small
/// fixed set of non-actor Player.log lines that carry session-level state
/// transitions — the lines that drive <see cref="ISessionAnchor"/> /
/// <c>PlayerAreaTracker</c> / etc. — distinct from the high-volume
/// actor-tier traffic (<see cref="LocalPlayerLogLine"/> /
/// <see cref="CombatActorLogLine"/>) and from cheap-discard engine noise.
/// </summary>
public enum SystemSignalKind
{
    /// <summary>
    /// <c>[ts] LOADING LEVEL &lt;Area&gt;</c>. Area-change marker.
    /// </summary>
    AreaLoading,

    /// <summary>
    /// <c>[ts] Logged in as character &lt;Name&gt;. Time UTC=… Timezone Offset …</c>.
    /// The login banner — <see cref="ISessionAnchor"/> /
    /// <see cref="PlayerLogClock"/>'s authoritative date source.
    /// </summary>
    LoginBanner,

    /// <summary>
    /// <c>[ts] LocalPlayer: ProcessAddPlayer(…)</c>. Distinguished from the
    /// <see cref="LocalPlayerLogLine"/> pipe because PG also emits this for
    /// the local player's own appearance at session start — i.e. it doubles
    /// as a login-completion signal alongside <see cref="LoginBanner"/>.
    /// </summary>
    PlayerAdded,

    /// <summary>
    /// <c>EVENT(Ok): loginCharacter | playing | sessionUpdate</c> — in-window,
    /// no-<c>[ts]</c> session-lifecycle phases. The pre-login preamble's
    /// <c>EVENT(Ok): connected</c> is routed to
    /// <see cref="ConnectionEvent"/> separately (#611). Only reaches L0.5
    /// when L0's session-replay window opens at byte 0 (Mithril attached
    /// before PG launched, or PG-truncation-while-Mithril-runs); a Mithril
    /// cold-start mid-PG-session seeks past the preamble and never sees it.
    /// </summary>
    SessionLifecycle,

    /// <summary>
    /// <c>Servers: [ { … }, … ]</c> — the JSON array of PG world servers
    /// emitted once at startup after the client fetches
    /// <c>clientconfig.json</c>. No <c>[ts]</c> prefix; lives in the preamble
    /// before the login banner. Consumed by <c>ServerCatalogService</c>
    /// (#610) to populate the <c>Url → ServerEntry</c> catalog that
    /// <c>ConnectionEventParser</c> (#611) joins against. Only reaches L0.5
    /// when L0's session-replay window opens at byte 0 (Mithril attached
    /// before PG launched, or PG-truncation-while-Mithril-runs); a Mithril
    /// cold-start mid-PG-session seeks past the preamble and never sees it,
    /// in which case the catalog stays empty for the attach lifetime.
    /// </summary>
    Servers,

    /// <summary>
    /// <c>EVENT(Ok): connected, url=&lt;host&gt;, port=&lt;port&gt;</c> — the
    /// per-session connect line PG emits when the client establishes its
    /// game-server TCP connection. No <c>[ts]</c> prefix; lives in the
    /// preamble before the login banner (~17 minutes earlier in some
    /// captures, due to character-select / area-load latency). Consumed by
    /// <c>ConnectionEventParser</c> + <c>GameSessionService</c> (#611) to
    /// derive the per-session server identity by joining against
    /// <c>IServerCatalogService</c> (#610). Same preamble caveat as
    /// <see cref="Servers"/>: only reaches L0.5 when the seed opens at
    /// byte 0; a Mithril cold-start mid-PG-session seeks past it and
    /// <c>GameSession.Server</c> stays <c>null</c> for the attach lifetime.
    /// </summary>
    ConnectionEvent,
}
