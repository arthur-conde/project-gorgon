namespace Mithril.Shared.Reference;

/// <summary>
/// Projection of one entry in quests.json. Drives Quest source resolution in
/// <c>sources_items.json</c> (questId → InternalName) and the future favor
/// planner / quest-tracker modules.
/// </summary>
public sealed record QuestEntry(
    string Key,
    string Name,
    string InternalName,
    string Description,
    string? DisplayedLocation,
    string? FavorNpc,
    IReadOnlyList<string> Keywords,
    IReadOnlyList<QuestObjective> Objectives,
    IReadOnlyList<QuestRequirement> Requirements,
    QuestRequirement? RequirementsToSustain,
    IReadOnlyList<QuestSkillReward> SkillRewards,
    IReadOnlyList<QuestItemReward> ItemRewards,
    int FavorReward,
    IReadOnlyList<string> RewardEffects,
    string? RewardLootProfile,
    int? ReuseMinutes,
    int? ReuseHours,
    int? ReuseDays,
    string? PrefaceText,
    string? SuccessText);

/// <summary>One step a quest requires the player to complete.</summary>
public sealed record QuestObjective(
    string Type,
    string Description,
    int Number,
    string? Target,
    string? ItemName,
    int? GroupId);

/// <summary>
/// Polymorphic quest prerequisite. <see cref="Type"/> carries the JSON's <c>T</c>
/// discriminator (<c>QuestCompleted</c>, <c>MinFavorLevel</c>, <c>MinSkillLevel</c>,
/// <c>HasEffectKeyword</c>). Only the conditional fields relevant to the discriminator
/// are populated.
/// </summary>
public sealed record QuestRequirement(
    string Type,
    string? Quest,
    string? Level,
    string? Npc,
    string? Skill,
    string? Keyword);

public sealed record QuestSkillReward(string Skill, int Xp);

public sealed record QuestItemReward(string ItemInternalName, int StackSize);
