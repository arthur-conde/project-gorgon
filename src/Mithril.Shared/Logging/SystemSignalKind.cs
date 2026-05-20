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
    /// <c>EVENT(Ok): connected</c> is structurally outside the L0 replay
    /// window and is handled by #514's seed facility, not here.
    /// </summary>
    SessionLifecycle,
}
