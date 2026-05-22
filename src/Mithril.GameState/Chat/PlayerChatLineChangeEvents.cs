using Mithril.WorldSim;

namespace Mithril.GameState.Chat;

/// <summary>
/// Folder-emitted change event for an aggregated player-chat message (#603).
/// The chat <see cref="PlayerChatLineLogService"/> emits one of these per
/// player-chat frame applied, surfacing the message on the ChatWorld bus.
///
/// <para>Carries the channel name verbatim — useful for diagnostics and
/// forward-compatibility (a future channel-aware consumer can subscribe to
/// these and filter by channel). The WoP view does not gate on channel; it
/// scans every player-chat message body for uppercase tokens, validated
/// against the codebook (see <c>docs/world-simulator.md</c> §Worked example 2).</para>
///
/// <para><b>Naming.</b> Past-tense participle per #657; mandatory <c>Chat</c>
/// world prefix because this is a folder-emitted event on the ChatWorld bus.</para>
/// </summary>
/// <param name="Channel">The channel name verbatim (no brackets, no normalisation).</param>
/// <param name="Speaker">The speaker name. May be empty for shapes lacking the
/// canonical <c>Speaker:</c> split.</param>
/// <param name="Text">The aggregated message body (continuation lines joined
/// with newlines).</param>
/// <param name="Timestamp">UTC timestamp of the parent prefixed line.</param>
public readonly record struct ChatPlayerLineObserved(
    string Channel,
    string Speaker,
    string Text,
    DateTime Timestamp) : IChangeEvent;
