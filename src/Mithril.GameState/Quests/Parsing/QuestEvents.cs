using Mithril.Shared.Logging;

namespace Mithril.GameState.Quests.Parsing;

/// <summary>
/// Bulk login signal: the player's full quest journal as the server reports
/// it on character connect. Two ID lists ride along — WorkOrder bulletin-board
/// quests (list A) and everything else (list B). Drives a snapshot-replace
/// of the active-quest set in <c>IPlayerQuestJournalService</c> so the journal
/// stays in sync with the game across restarts and character switches.
/// </summary>
public sealed record QuestJournalLoadedEvent(
    DateTime Timestamp,
    IReadOnlyList<int> WorkOrderQuestIds,
    IReadOnlyList<int> RegularQuestIds)
    : LogEvent(Timestamp);

/// <summary>
/// Player accepted a quest. Recovered from the companion <c>ProcessBook</c>
/// line that fires alongside <c>ProcessAddQuest</c> — the unresolved
/// <c>&lt;&lt;&lt;quest_NNNNN_Name&gt;&gt;&gt;</c> localization template carries the quest id.
/// Drives an incremental add to <c>IPlayerQuestJournalService.ActiveQuests</c>.
/// </summary>
public sealed record QuestAcceptedEvent(DateTime Timestamp, string QuestInternalName)
    : LogEvent(Timestamp);

/// <summary>
/// Player completed (turned in) a repeatable quest. Removes the quest from the
/// active journal and stamps <c>IPlayerQuestJournalService.CompletionHistory</c> with this
/// timestamp; downstream cooldown clocks anchor on it.
/// </summary>
public sealed record QuestCompletedEvent(DateTime Timestamp, string QuestInternalName)
    : LogEvent(Timestamp);
