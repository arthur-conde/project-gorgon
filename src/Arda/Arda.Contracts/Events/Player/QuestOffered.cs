using Arda.Abstractions.Logs;

namespace Arda.World.Player.Events;

/// <summary>
/// Emitted when a quest acceptance <c>ProcessBook("New Quest: ...")</c> is
/// observed. Carries the raw quest ID extracted from the localization template
/// <c>&lt;&lt;&lt;quest_NNNNN_Name&gt;&gt;&gt;</c>. Name resolution (quest ID → InternalName)
/// is consumer-side via reference data.
/// </summary>
public readonly record struct QuestOffered(
    int QuestId,
    LogLineMetadata Metadata);
