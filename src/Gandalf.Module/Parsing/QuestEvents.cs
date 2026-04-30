using Mithril.Shared.Logging;

namespace Gandalf.Parsing;

/// <summary>
/// Bulk login signal: the player's full quest journal as the server reports
/// it on character connect. Two ID lists ride along — WorkOrder bulletin-board
/// quests (list A) and everything else (list B). Drives the snapshot-replace
/// of <c>QuestSource._pending</c> so the "Pending" filter chip works after a
/// fresh login.
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
/// Drives the incremental add to <c>QuestSource._pending</c>.
/// </summary>
public sealed record QuestAcceptedEvent(DateTime Timestamp, string QuestInternalName)
    : LogEvent(Timestamp);

/// <summary>
/// Player completed (turned in) a repeatable quest. Anchors the cooldown clock
/// — <c>StartedAt</c> on the resulting timer is this Timestamp (past-anchored
/// via <c>DerivedTimerProgressService</c>).
/// </summary>
public sealed record QuestCompletedEvent(DateTime Timestamp, string QuestInternalName)
    : LogEvent(Timestamp);
