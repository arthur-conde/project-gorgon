namespace Mithril.Reference.Models.Abilities;

/// <summary>
/// Polymorphic prerequisite row in an ability's <c>SpecialCasterRequirements</c>
/// field. Discriminated by <c>T</c>. Several T-values overlap by name with
/// <see cref="Quests.QuestRequirement"/> and
/// <see cref="Recipes.RecipeRequirement"/> (HasEffectKeyword, IsVampire,
/// IsLongtimeAnimal, etc.); kept as a separate hierarchy by the same
/// field-set-divergence rule that separated the others.
/// </summary>
public abstract class AbilitySpecialCasterRequirement
{
    public string T { get; set; } = "";
}

public sealed class UnknownAbilitySpecialCasterRequirement
    : AbilitySpecialCasterRequirement, IUnknownDiscriminator
{
    public string DiscriminatorValue { get; set; } = "";
}

public sealed class EffectKeywordUnsetAbilityRequirement : AbilitySpecialCasterRequirement
{
    public string? Keyword { get; set; }
}

public sealed class EquippedItemKeywordAbilityRequirement : AbilitySpecialCasterRequirement
{
    public string? Keyword { get; set; }
    public int? MaxCount { get; set; }
    public int? MinCount { get; set; }
}

public sealed class HasEffectKeywordAbilityRequirement : AbilitySpecialCasterRequirement
{
    public string? Keyword { get; set; }
}

public sealed class HasInventorySpaceForRequirement : AbilitySpecialCasterRequirement
{
    public string? Item { get; set; }
}

public sealed class InHotspotAbilityRequirement : AbilitySpecialCasterRequirement
{
    public string? Name { get; set; }
}

public sealed class InMusicPerformanceRequirement : AbilitySpecialCasterRequirement { }

public sealed class InteractionFlagSetAbilityRequirement : AbilitySpecialCasterRequirement
{
    public string? InteractionFlag { get; set; }
}

public sealed class InventoryItemKeywordRequirement : AbilitySpecialCasterRequirement
{
    public string? Keyword { get; set; }
}

public sealed class IsDancingOnPoleRequirement : AbilitySpecialCasterRequirement { }

public sealed class IsHardcoreAbilityRequirement : AbilitySpecialCasterRequirement { }

public sealed class IsLongtimeAnimalAbilityRequirement : AbilitySpecialCasterRequirement { }

public sealed class IsNotGuestAbilityRequirement : AbilitySpecialCasterRequirement { }

public sealed class IsNotInCombatRequirement : AbilitySpecialCasterRequirement { }

public sealed class IsNotInHotspotRequirement : AbilitySpecialCasterRequirement
{
    public string? Name { get; set; }
}

public sealed class IsVampireAbilityRequirement : AbilitySpecialCasterRequirement { }

public sealed class IsVegetarianRequirement : AbilitySpecialCasterRequirement { }

public sealed class IsVolunteerGuideRequirement : AbilitySpecialCasterRequirement { }
