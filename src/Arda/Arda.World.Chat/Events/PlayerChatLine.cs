using Arda.Abstractions.Logs;

namespace Arda.World.Chat.Events;

/// <summary>
/// Emitted for each player chat line (Tier 2 passthrough). Carries the
/// channel, speaker, and message text as slices of the source string.
/// </summary>
public readonly record struct PlayerChatLine(
    string Channel,
    string Speaker,
    string Text,
    LogLineMetadata Metadata);
