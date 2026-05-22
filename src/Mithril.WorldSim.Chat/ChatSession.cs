namespace Mithril.WorldSim.Chat;

/// <summary>
/// Identifies a PG chat session by the <c>(Server, Character)</c> pair declared
/// on the chat-side login banner <c>**** Logged In As X. Server Y. Timezone
/// Offset Z.</c> Banner-derived: chat's own intra-source self-scope (principle 7).
/// </summary>
/// <param name="Server">
/// PG server name as declared on the banner (e.g., <c>Laeth</c>). Verbatim
/// from the log — no normalisation, since the same string is what
/// <see cref="IServerCatalogService"/> would key on for cross-source agreement
/// checks at the view layer.
/// </param>
/// <param name="Character">
/// Character name as declared on the banner (e.g., <c>Emraell</c>). Verbatim
/// from the log.
/// </param>
/// <param name="At">
/// Banner-line timestamp (chat-prefix-derived). The
/// <see cref="Mithril.Shared.Logging.RawLogLine.Timestamp"/> of the banner
/// line itself — TZ-correct via <see cref="Mithril.Shared.Logging.ChatLogClock"/>
/// folding the <c>yy-MM-dd HH:mm:ss\t</c> prefix over <see cref="Offset"/>.
/// </param>
/// <param name="Offset">
/// Originating session's <c>Timezone Offset</c> (signed <c>HH:MM:SS</c>).
/// Captured verbatim from the banner's <c>Timezone Offset</c> field; used by
/// <see cref="Mithril.Shared.Logging.ChatLogClock"/> for cross-machine replay
/// timestamp folding (#538).
/// </param>
public sealed record ChatSession(
    string Server,
    string Character,
    DateTimeOffset At,
    TimeSpan Offset);
