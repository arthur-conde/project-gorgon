using System;
using System.Collections.Generic;
using Mithril.Reference.Models.Quests;
using Mithril.Reference.Serialization.Converters;

namespace Mithril.Reference.Serialization.Discriminators;

/// <summary>
/// Maps the JSON <c>T</c> discriminator strings to concrete
/// <see cref="QuestRequirement"/> and <see cref="QuestReward"/> subclasses.
/// Adding a new discriminator value: drop a subclass into
/// <c>Models/Quests/</c> and register it here. The validation harness will
/// surface any unmapped discriminator on next test run.
/// </summary>
internal static class QuestDiscriminators
{
    public static DiscriminatedUnionConverter<QuestRequirement, UnknownQuestRequirement>
        BuildRequirementConverter()
        => new("T", RequirementMap);

    public static DiscriminatedUnionConverter<QuestReward, UnknownQuestReward>
        BuildRewardConverter()
        => new("T", RewardMap);

    private static readonly IReadOnlyDictionary<string, Type> RequirementMap = new Dictionary<string, Type>
    {
        ["MinSkillLevel"] = typeof(MinSkillLevelRequirement),
        ["MinFavorLevel"] = typeof(MinFavorLevelRequirement),
        ["QuestCompleted"] = typeof(QuestCompletedRequirement),
        ["HasEffectKeyword"] = typeof(HasEffectKeywordRequirement),
        ["RuntimeBehaviorRuleSet"] = typeof(RuntimeBehaviorRuleSetRequirement),
        ["Race"] = typeof(RaceRequirement),
        ["ScriptAtomicMatches"] = typeof(ScriptAtomicMatchesRequirement),
        ["AreaEventOff"] = typeof(AreaEventOffRequirement),
        ["IsVampire"] = typeof(IsVampireRequirement),
        ["InteractionFlagSet"] = typeof(InteractionFlagSetRequirement),
        ["Or"] = typeof(OrRequirement),
        ["QuestCompletedRecently"] = typeof(QuestCompletedRecentlyRequirement),
        ["AreaEventOn"] = typeof(AreaEventOnRequirement),
        ["GuildQuestCompleted"] = typeof(GuildQuestCompletedRequirement),
        ["IsWarden"] = typeof(IsWardenRequirement),
        ["MoonPhase"] = typeof(MoonPhaseRequirement),
        ["IsLongtimeAnimal"] = typeof(IsLongtimeAnimalRequirement),
        ["IsNotGuest"] = typeof(IsNotGuestRequirement),
        ["HangOutCompleted"] = typeof(HangOutCompletedRequirement),
        ["DayOfWeek"] = typeof(DayOfWeekRequirement),
        ["GeneralShape"] = typeof(GeneralShapeRequirement),
        ["InteractionFlagUnset"] = typeof(InteractionFlagUnsetRequirement),
        ["MinFavor"] = typeof(MinFavorRequirement),
        ["AccountFlagUnset"] = typeof(AccountFlagUnsetRequirement),
        ["Appearance"] = typeof(AppearanceRequirement),
        ["MinCombatSkillLevel"] = typeof(MinCombatSkillLevelRequirement),
        ["AttributeMatchesScriptAtomic"] = typeof(AttributeMatchesScriptAtomicRequirement),
        ["InventoryItem"] = typeof(InventoryItemRequirement),
        ["EquipmentSlotEmpty"] = typeof(EquipmentSlotEmptyRequirement),
        ["EquippedItemKeyword"] = typeof(EquippedItemKeywordRequirement),
        ["ActiveCombatSkill"] = typeof(ActiveCombatSkillRequirement),
        ["TimeOfDay"] = typeof(TimeOfDayRequirement),
        ["MonsterTargetLevel"] = typeof(MonsterTargetLevelRequirement),
        ["FullMoon"] = typeof(FullMoonRequirement),
        ["InHotspot"] = typeof(InHotspotRequirement),
        ["HasMountInStable"] = typeof(HasMountInStableRequirement),
        ["InCombat"] = typeof(InCombatRequirement),
        ["InCombatWithElite"] = typeof(InCombatWithEliteRequirement),
        ["OtherHasTypeTag"] = typeof(OtherHasTypeTagRequirement),
        ["AbilityKnown"] = typeof(AbilityKnownRequirement),
        ["PetCount"] = typeof(PetCountRequirement),
        ["EntityPhysicalState"] = typeof(EntityPhysicalStateRequirement),
    };

    private static readonly IReadOnlyDictionary<string, Type> RewardMap = new Dictionary<string, Type>
    {
        ["SkillXp"] = typeof(SkillXpReward),
        ["WorkOrderCurrency"] = typeof(WorkOrderCurrencyReward),
        ["Currency"] = typeof(CurrencyReward),
        ["Recipe"] = typeof(RecipeReward),
        ["CombatXp"] = typeof(CombatXpReward),
        ["GuildXp"] = typeof(GuildXpReward),
        ["GuildCredits"] = typeof(GuildCreditsReward),
        ["RacingXp"] = typeof(RacingXpReward),
        ["Ability"] = typeof(AbilityReward),
    };
}
