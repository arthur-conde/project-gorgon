namespace Mithril.WorldSim.Chat.Producers;

/// <summary>
/// Successful parse of a chat-side login banner — <c>**** Logged In As X.
/// Server Y. Timezone Offset Z.</c> — by <see cref="ChatLoginBannerParser"/>.
/// Distinct from <see cref="ChatSession"/> in that this is a per-line parser
/// output; <see cref="ChatSession"/> is the session-service-tracked
/// canonical state (the producer projects every parser hit into a
/// <see cref="ChatSession"/> by attaching the banner-line's timestamp).
/// </summary>
/// <param name="Server">PG server name as declared on the banner.</param>
/// <param name="Character">Character name as declared on the banner.</param>
/// <param name="Offset">
/// Signed <c>HH:MM:SS</c> timezone offset as declared on the banner.
/// </param>
public readonly record struct ChatLoginBanner(
    string Server,
    string Character,
    TimeSpan Offset);
