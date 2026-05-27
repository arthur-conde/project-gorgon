using Arda.Abstractions.Logs;

namespace Arda.World.Chat.Events;

/// <summary>
/// Emitted when a login banner is observed in the chat log, identifying
/// the active character and server.
/// </summary>
public readonly record struct ChatSessionIdentified(
    string Character,
    string Server,
    TimeSpan TimezoneOffset,
    LogLineMetadata Metadata);
