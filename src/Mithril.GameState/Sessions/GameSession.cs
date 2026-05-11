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
public sealed record GameSession(
    string SessionId,
    string CharacterName,
    DateTime LoggedInUtc,
    TimeSpan TimezoneOffset);
