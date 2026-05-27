using Arda.Abstractions.Logs;

namespace Arda.World.Player.Events;

/// <summary>
/// Emitted when a quest is accepted (via <c>ProcessBook("New Quest: ...")</c>).
/// The quest is added to the active quest journal.
/// </summary>
public readonly record struct QuestAccepted(
    int QuestId,
    LogLineMetadata Metadata);
