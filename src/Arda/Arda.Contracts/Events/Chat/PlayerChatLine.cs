using Arda.Abstractions.Logs;

namespace Arda.World.Chat.Events;

/// <summary>
/// Emitted for each player chat line. <see cref="Channel"/> and
/// <see cref="Speaker"/> are interned strings (Tier 1); <see cref="Text"/>
/// is a zero-copy <see cref="ReadOnlyMemory{T}"/> slice of the source log
/// line (Tier 2). Consumers that need a <see cref="string"/> for <c>Text</c>
/// call <see cref="ReadOnlyMemory{T}.ToString()"/> at the consumption site.
/// </summary>
public readonly record struct PlayerChatLine(
    string Channel,
    string Speaker,
    ReadOnlyMemory<char> Text,
    LogLineMetadata Metadata);
