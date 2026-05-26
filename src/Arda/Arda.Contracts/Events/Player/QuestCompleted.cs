using Arda.Abstractions.Logs;

namespace Arda.World.Player.Events;

/// <summary>
/// Emitted when a quest is completed/turned in via
/// <c>ProcessCompleteQuest(charEntityId, questId)</c>.
/// The quest is removed from the active quest journal.
/// </summary>
public readonly record struct QuestCompleted(
    int QuestId,
    LogLineMetadata Metadata);
