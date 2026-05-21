using Mithril.GameState.Servers;

namespace Mithril.GameState.Sessions;

/// <summary>
/// Identity + temporal metadata for a single PG game session, parsed from the
/// <c>Logged in as character … Time UTC=… Timezone Offset …</c> banner that
/// PG emits at every login.
///
/// <see cref="SessionId"/> is the natural dedup key for log-derived
/// observations (Arwen gifts, Smaug sells, …). It collapses to the same value
/// every time the same banner is parsed, so replay-on-relaunch attributes the
/// same physical login to the same id and downstream services short-circuit
/// duplicate writes.
/// </summary>
/// <param name="SessionId">
/// Stable, parser-derived natural key for the session — collapses to the
/// same value across replays of the same physical login. Independent of
/// <see cref="Server"/>: two replays of the same banner mint the same id
/// even if the server identity changes between observations (which doesn't
/// happen in PG, but the contract isolates the two concerns).
/// </param>
/// <param name="CharacterName">
/// The character logged in, as PG emitted it in the banner.
/// </param>
/// <param name="LoggedInUtc">
/// The login instant, in UTC, parsed from the banner's <c>Time UTC=…</c>
/// field. The authoritative date source for <see cref="Mithril.Shared.Logging.PlayerLogClock"/>.
/// </param>
/// <param name="TimezoneOffset">
/// The host's local TZ offset at the time PG wrote the banner. Closes the
/// Player.log-UTC vs ChatLogs-local asymmetry documented in
/// <c>pg_log_timezones</c>.
/// </param>
/// <param name="Server">
/// The PG world server for this session, resolved by joining the per-session
/// <c>EVENT(Ok): connected, url=…, port=…</c> preamble line against
/// <see cref="IServerCatalogService"/>. Added by #611.
///
/// <para><b>May be <c>null</c>.</b> The connect line lives in the L0
/// preamble, before the login banner. When Mithril cold-starts mid-PG-
/// session, L0's seed strategy seeks past the preamble to the most-recent
/// session marker (banner / <c>ProcessAddPlayer</c>), so neither the
/// <c>Servers:</c> catalog line nor <c>EVENT(Ok): connected</c> reaches
/// L0.5 — server identity is unknown for that attach lifetime, and
/// <see cref="Server"/> stays <c>null</c>. Consumers must handle this
/// explicitly rather than expect a fabricated default.</para>
///
/// <para>Even when the preamble IS in the replay window, the catalog may
/// be empty if the <c>Servers:</c> line failed to parse (a future PG
/// patch breaking the JSON shape) — in that case
/// <see cref="IServerCatalogService.Get"/> returns <c>null</c> and the
/// field stays <c>null</c> for the session. The connect URL itself is
/// recorded in diagnostics for that path.</para>
/// </param>
public sealed record GameSession(
    string SessionId,
    string CharacterName,
    DateTime LoggedInUtc,
    TimeSpan TimezoneOffset,
    ServerEntry? Server = null);
