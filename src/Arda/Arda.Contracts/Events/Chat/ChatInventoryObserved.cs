using Arda.Abstractions.Logs;

namespace Arda.World.Chat.Events;

/// <summary>
/// Emitted when a <c>[Status] X [xN] added to inventory.</c> line is observed.
/// Carries the display name and stack count from the chat message.
/// </summary>
public readonly record struct ChatInventoryObserved(
    string DisplayName,
    int Count,
    LogLineMetadata Metadata);
