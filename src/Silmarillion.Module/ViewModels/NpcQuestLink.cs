namespace Silmarillion.ViewModels;

/// <summary>
/// Display projection of a quest associated with an NPC for the NPCs tab detail pane.
/// Plain-text rendering until the Quests tab ships (#242) — at which point this is the spot
/// to swap in an <c>EntityRef.Quest</c>-anchored chip. v1 matches only on
/// <c>Quest.FavorNpc</c> (the giver/turn-in NPC); per-objective turn-in detection is a
/// follow-up.
/// </summary>
public sealed record NpcQuestLink(
    string DisplayName,
    string InternalName);
