using System.Collections.Generic;

namespace Mithril.Reference.Models.Quests;

/// <summary>
/// Polymorphic prerequisite row attached to a quest's <c>Requirements</c>,
/// <c>RequirementsToSustain</c>, or an objective's nested <c>Requirements</c>.
/// The JSON's <c>T</c> field discriminates between 42 concrete subclasses.
/// Unknown discriminators deserialize to <see cref="UnknownQuestRequirement"/>
/// — see <see cref="IUnknownDiscriminator"/> for the drift-detection contract.
/// </summary>
public abstract class QuestRequirement
{
    /// <summary>JSON discriminator. Always populated by the deserializer.</summary>
    public string T { get; set; } = "";
}

/// <summary>Sentinel for any <c>T</c> value not covered by a concrete subclass.</summary>
public sealed class UnknownQuestRequirement : QuestRequirement, IUnknownDiscriminator
{
    public string DiscriminatorValue { get; set; } = "";
}

// ─── Concrete subclasses (one per known T value) ──────────────────────────────
//
// Field sets are mirrored from the bundled JSON; properties are nullable where
// the field is optional in the data. Property names match the JSON exactly so
// no contract-resolver remapping is needed.

public sealed class MinSkillLevelRequirement : QuestRequirement
{
    /// <summary>String-or-int in JSON; coerced to string by StringOrIntStringConverter.</summary>
    public string? Level { get; set; }
    public string? Skill { get; set; }
}

public sealed class MinFavorLevelRequirement : QuestRequirement
{
    public string? Level { get; set; }
    public string? Npc { get; set; }
}

public sealed class QuestCompletedRequirement : QuestRequirement
{
    public string? Quest { get; set; }
    public int? MinDelayAfterFirstCompletion_Hours { get; set; }
}

public sealed class HasEffectKeywordRequirement : QuestRequirement
{
    public string? Keyword { get; set; }
}

public sealed class RuntimeBehaviorRuleSetRequirement : QuestRequirement
{
    public string? Rule { get; set; }
}

public sealed class RaceRequirement : QuestRequirement
{
    public string? AllowedRace { get; set; }
    public string? DisallowedRace { get; set; }
}

public sealed class ScriptAtomicMatchesRequirement : QuestRequirement
{
    public string? AtomicVar { get; set; }

    /// <summary>String-or-int in JSON; coerced to string.</summary>
    public string? Value { get; set; }
}

public sealed class AreaEventOffRequirement : QuestRequirement
{
    public string? AreaEvent { get; set; }
}

public sealed class IsVampireRequirement : QuestRequirement { }

public sealed class InteractionFlagSetRequirement : QuestRequirement
{
    public string? InteractionFlag { get; set; }
}

public sealed class OrRequirement : QuestRequirement
{
    public IReadOnlyList<QuestRequirement>? List { get; set; }
}

public sealed class QuestCompletedRecentlyRequirement : QuestRequirement
{
    public string? Quest { get; set; }
}

public sealed class AreaEventOnRequirement : QuestRequirement
{
    public string? AreaEvent { get; set; }
}

public sealed class GuildQuestCompletedRequirement : QuestRequirement
{
    public string? Quest { get; set; }
}

public sealed class IsWardenRequirement : QuestRequirement { }

public sealed class MoonPhaseRequirement : QuestRequirement
{
    public string? MoonPhase { get; set; }
}

public sealed class IsLongtimeAnimalRequirement : QuestRequirement { }

public sealed class IsNotGuestRequirement : QuestRequirement { }

public sealed class HangOutCompletedRequirement : QuestRequirement
{
    public string? HangOut { get; set; }
}

public sealed class DayOfWeekRequirement : QuestRequirement
{
    public IReadOnlyList<string>? DaysAllowed { get; set; }
}

public sealed class GeneralShapeRequirement : QuestRequirement
{
    public string? Shape { get; set; }
}

public sealed class InteractionFlagUnsetRequirement : QuestRequirement
{
    public string? InteractionFlag { get; set; }
}

public sealed class MinFavorRequirement : QuestRequirement
{
    public string? Npc { get; set; }
    public int? MinFavor { get; set; }
}

public sealed class AccountFlagUnsetRequirement : QuestRequirement
{
    public string? AccountFlag { get; set; }
}

public sealed class AppearanceRequirement : QuestRequirement
{
    public string? Appearance { get; set; }
}

public sealed class MinCombatSkillLevelRequirement : QuestRequirement
{
    public int? Level { get; set; }
}

public sealed class AttributeMatchesScriptAtomicRequirement : QuestRequirement
{
    public string? Attribute { get; set; }
    public string? ScriptAtomicInt { get; set; }
}

public sealed class InventoryItemRequirement : QuestRequirement
{
    public string? Item { get; set; }
}

// ─── Objective-level requirement T values ─────────────────────────────────────

public sealed class EquipmentSlotEmptyRequirement : QuestRequirement
{
    public string? Slot { get; set; }
}

public sealed class EquippedItemKeywordRequirement : QuestRequirement
{
    public string? Keyword { get; set; }
}

public sealed class ActiveCombatSkillRequirement : QuestRequirement
{
    public string? Skill { get; set; }
    public string? AllowSkill { get; set; }
}

public sealed class TimeOfDayRequirement : QuestRequirement
{
    public string? Hours { get; set; }
    public int? MinHour { get; set; }
    public int? MaxHour { get; set; }
}

public sealed class MonsterTargetLevelRequirement : QuestRequirement
{
    public int? MinLevel { get; set; }
}

public sealed class FullMoonRequirement : QuestRequirement { }

public sealed class InHotspotRequirement : QuestRequirement
{
    public string? Name { get; set; }
}

public sealed class HasMountInStableRequirement : QuestRequirement
{
    public int? MinimumMountsNeeded { get; set; }
}

public sealed class InCombatRequirement : QuestRequirement
{
    public int? MinLevel { get; set; }
}

public sealed class InCombatWithEliteRequirement : QuestRequirement
{
    public int? MinLevel { get; set; }
}

public sealed class OtherHasTypeTagRequirement : QuestRequirement
{
    public string? TypeTag { get; set; }
}

public sealed class AbilityKnownRequirement : QuestRequirement
{
    public string? Ability { get; set; }
}

public sealed class PetCountRequirement : QuestRequirement
{
    public string? PetTypeTag { get; set; }
    public int? MinCount { get; set; }
    public int? MaxCount { get; set; }
}

public sealed class EntityPhysicalStateRequirement : QuestRequirement
{
    public IReadOnlyList<string>? AllowedStates { get; set; }
}
