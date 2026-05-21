using Mithril.Shared.Logging;

namespace Mithril.GameState.Sessions.Parsing;

/// <summary>
/// The parsed <c>EVENT(Ok): connected, url=&lt;host&gt;, port=&lt;port&gt;</c>
/// preamble line PG emits when the client establishes its game-server TCP
/// connection. Carries the connection's host + port; the
/// <c>GameSessionService</c> consumer joins <see cref="Url"/> against
/// <see cref="Mithril.GameState.Servers.IServerCatalogService"/> to derive
/// the friendly server name surfaced on <c>GameSession.Server</c>.
///
/// <para>PG emits this once per session, in the preamble before the
/// <c>Logged in as character</c> banner (~17 minutes earlier in some
/// captures, due to character-select / area-load latency). It only reaches
/// L0.5 when the L0 seed opens at byte 0 — see <c>SystemSignalKind.ConnectionEvent</c>.</para>
/// </summary>
/// <param name="Timestamp">
/// The wall-clock instant PG emitted the line. Preamble lines have no
/// <c>[ts]</c> prefix, so this comes from the L0 source clock's best-effort
/// stamp at the time the line was read (file mtime fallback, since no
/// in-line date is available pre-banner).
/// </param>
/// <param name="Url">
/// The connection host as PG wrote it (e.g. <c>s4.projectgorgon.com</c>).
/// The join key for <see cref="Mithril.GameState.Servers.IServerCatalogService.Get"/>;
/// preserved verbatim so a lookup-against-catalog reproduces PG's exact
/// host string.
/// </param>
/// <param name="Port">
/// The TCP port the client connected to. Currently always 9002 in observed
/// PG traffic, but the parser preserves whatever PG emitted so a future PG
/// patch that uses a different port surfaces unchanged.
/// </param>
public sealed record ConnectionEvent(DateTime Timestamp, string Url, int Port)
    : LogEvent(Timestamp);
