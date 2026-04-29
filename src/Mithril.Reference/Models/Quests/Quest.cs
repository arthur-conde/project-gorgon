using System.Collections.Generic;

namespace Mithril.Reference.Models.Quests;

/// <summary>
/// One quest entry from <c>quests.json</c>. Property names match the JSON
/// exactly — including underscored variants (<c>Reward_Favor</c>,
/// <c>ReuseTime_Days</c>) — so no contract-resolver remapping is required.
/// Polymorphic fields (<see cref="Requirements"/>, <see cref="RequirementsToSustain"/>,
/// <see cref="Rewards"/>) carry the concrete subclasses; see
/// <see cref="QuestRequirement"/> and <see cref="QuestReward"/>.
/// </summary>
public sealed class Quest
{
    // ─── Always-present fields (per the bundled JSON: 2981/2981 entries) ──
    public string? Description { get; set; }
    public string? InternalName { get; set; }
    public string? Name { get; set; }
    public IReadOnlyList<QuestObjective>? Objectives { get; set; }
    public int Version { get; set; }

    // ─── Optional fields (sorted by frequency in the bundled data) ──
    public string? SuccessText { get; set; }
    public bool? IsCancellable { get; set; }

    /// <summary>Dict-or-array in JSON; coerced to a list by SingleOrArrayConverter.</summary>
    public IReadOnlyList<QuestRequirement>? Requirements { get; set; }

    public IReadOnlyList<QuestReward>? Rewards { get; set; }
    public IReadOnlyList<string>? Keywords { get; set; }
    public int? ReuseTime_Days { get; set; }
    public string? PrefaceText { get; set; }
    public string? WorkOrderSkill { get; set; }
    public string? DisplayedLocation { get; set; }
    public string? FavorNpc { get; set; }
    public int? Reward_Favor { get; set; }
    public IReadOnlyList<QuestItemRef>? Rewards_Items { get; set; }
    public bool? IsAutoWrapUp { get; set; }
    public bool? IsAutoPreface { get; set; }
    public IReadOnlyList<string>? Rewards_Effects { get; set; }
    public int? TSysLevel { get; set; }
    public int? ReuseTime_Hours { get; set; }

    /// <summary>Dict-or-array in JSON; coerced to a list by SingleOrArrayConverter.</summary>
    public IReadOnlyList<QuestRequirement>? RequirementsToSustain { get; set; }

    public string? Rewards_NamedLootProfile { get; set; }
    public string? MidwayText { get; set; }
    public bool? ForceBookOnWrapUp { get; set; }
    public int? ReuseTime_Minutes { get; set; }
    public IReadOnlyList<string>? FollowUpQuests { get; set; }
    public bool? DeleteFromHistoryIfVersionChanged { get; set; }
    public string? GroupingName { get; set; }
    public string? MainNpcName { get; set; }
    public int? NumExpectedParticipants { get; set; }
    public IReadOnlyList<QuestItemRef>? PreGiveItems { get; set; }
    public IReadOnlyList<string>? PreGiveEffects { get; set; }
    public string? QuestNpc { get; set; }
    public bool? IsGuildQuest { get; set; }
    public IReadOnlyList<string>? PreGiveRecipes { get; set; }
    public int? Level { get; set; }
    public string? Rewards_Description { get; set; }
    public IReadOnlyList<string>? QuestFailEffects { get; set; }
    public string? PrerequisiteFavorLevel { get; set; }
    public int? Rewards_Favor { get; set; }
    public bool? CheckRequirementsToSustainOnBestow { get; set; }
    public IReadOnlyList<QuestItemRef>? MidwayGiveItems { get; set; }

    /// <summary>Map from skill internal name to skill-level reward count. Rare (n=1).</summary>
    public IReadOnlyDictionary<string, int>? Reward_SkillLevels { get; set; }
}
