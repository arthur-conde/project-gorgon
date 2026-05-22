namespace Mithril.GameState.Chat;

/// <summary>
/// World-simulator frame payload for the player-chat folder
/// (<see cref="PlayerChatLineLogService"/>) — #603. A sibling
/// <see cref="Producers.PlayerChatLineProducer"/> reads chat
/// <see cref="Mithril.Shared.Logging.RawLogLine"/> envelopes, aggregates
/// continuation lines into their parent message, classifies the channel via
/// <see cref="PlayerChatChannelClassifier"/>, and emits one frame per logical
/// player-chat message.
///
/// <para><b>Classifier rule</b> — allowlist for system buckets (<c>[Status]</c>,
/// <c>[NPC Chatter]</c>, … are NOT player chat and are drained at the producer
/// level), catch-all for player chat: any unrecognised channel falls into
/// PlayerChat by default. The channel name is carried verbatim
/// (whatever's between the brackets, no normalisation) as the
/// <see cref="Channel"/> field so consumers can compare against their own
/// allowlist or surface the room of origin for diagnostics.</para>
/// </summary>
/// <param name="Channel">The channel name verbatim (no brackets, no
/// normalisation). Examples: <c>Help</c>, <c>Trade</c>, <c>Local</c>,
/// <c>Whisper</c>, <c>Group</c>, <c>Party</c>, <c>Global</c>, custom rooms like
/// <c>woptraders</c>.</param>
/// <param name="Speaker">The speaker name parsed from <c>[Channel] Speaker:
/// text</c>. Empty when the line shape lacks a colon — defensive default; not
/// expected in practice.</param>
/// <param name="Text">The message body — continuation lines aggregated into the
/// parent message with a literal newline between segments. The original
/// timestamp/channel prefix is stripped.</param>
public sealed record PlayerChatLineFrame(
    string Channel,
    string Speaker,
    string Text);
