namespace Arda.World.Player;

/// <summary>
/// A single quest in the player's active journal.
/// </summary>
public readonly record struct QuestEntry(int QuestId, DateTimeOffset AddedAt);

/// <summary>
/// Read-only view of the player's active quest journal. Session-scoped —
/// resets on character switch. Consumers needing change notifications
/// subscribe to <see cref="Events.QuestAccepted"/>,
/// <see cref="Events.QuestCompleted"/>, or <see cref="Events.QuestsLoaded"/>
/// via <see cref="Arda.Contracts.IDomainEventSubscriber"/>.
/// </summary>
public interface IQuestState
{
    /// <summary>Active quests keyed by quest ID.</summary>
    IReadOnlyDictionary<int, QuestEntry> ActiveQuests { get; }
}
