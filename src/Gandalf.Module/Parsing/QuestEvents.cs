using Mithril.Shared.Logging;

namespace Gandalf.Parsing;

/// <summary>
/// Player picked up (loaded into journal) a quest. Drives the "Pending" filter
/// chip in the Quests tab — quests that are in the journal but haven't been
/// completed-and-cooled.
/// </summary>
public sealed record QuestLoadedEvent(DateTime Timestamp, string QuestInternalName)
    : LogEvent(Timestamp);

/// <summary>
/// Player completed (turned in) a repeatable quest. Anchors the cooldown clock
/// — <c>StartedAt</c> on the resulting timer is this Timestamp (past-anchored
/// via <c>DerivedTimerProgressService</c>).
/// </summary>
public sealed record QuestCompletedEvent(DateTime Timestamp, string QuestInternalName)
    : LogEvent(Timestamp);
