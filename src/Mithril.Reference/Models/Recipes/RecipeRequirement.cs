using System.Collections.Generic;

namespace Mithril.Reference.Models.Recipes;

/// <summary>
/// Polymorphic prerequisite row in a recipe's <c>OtherRequirements</c> field.
/// Discriminated by the JSON <c>T</c> string. Although several T-values
/// overlap by name with <c>Mithril.Reference.Models.Quests.QuestRequirement</c>
/// (e.g. <c>HasEffectKeyword</c>, <c>EquipmentSlotEmpty</c>, <c>MoonPhase</c>),
/// field sets can differ slightly between the two domains so the hierarchies
/// stay separate. A future cross-domain refactor can merge the shared
/// requirement types into a common base if needed.
/// </summary>
public abstract class RecipeRequirement
{
    public string T { get; set; } = "";
}

/// <summary>Sentinel for any <c>T</c> value not covered by a concrete subclass.</summary>
public sealed class UnknownRecipeRequirement : RecipeRequirement, IUnknownDiscriminator
{
    public string DiscriminatorValue { get; set; } = "";
}

// ─── Concrete subclasses (one per known T value) ──────────────────────────────

public sealed class AlwaysFailRequirement : RecipeRequirement { }

public sealed class AppearanceRecipeRequirement : RecipeRequirement
{
    public string? Appearance { get; set; }
}

public sealed class DruidEventStateRequirement : RecipeRequirement
{
    public IReadOnlyList<string>? DisallowedStates { get; set; }
}

public sealed class EntitiesNearRequirement : RecipeRequirement
{
    public string? EntityTypeTag { get; set; }
    public int? Distance { get; set; }
    public int? MinCount { get; set; }
    public string? ErrorMsg { get; set; }
}

public sealed class EntityPhysicalStateRecipeRequirement : RecipeRequirement
{
    public IReadOnlyList<string>? AllowedStates { get; set; }
}

public sealed class EquipmentSlotEmptyRecipeRequirement : RecipeRequirement
{
    public string? Slot { get; set; }
}

public sealed class FullMoonRecipeRequirement : RecipeRequirement { }

public sealed class HasEffectKeywordRecipeRequirement : RecipeRequirement
{
    public string? Keyword { get; set; }
    public int? MinCount { get; set; }
}

public sealed class HasGuildHallRequirement : RecipeRequirement { }

public sealed class HasHandsRequirement : RecipeRequirement { }

public sealed class InGraveyardRequirement : RecipeRequirement { }

public sealed class IsHardcoreRequirement : RecipeRequirement { }

public sealed class IsLycanthropeRequirement : RecipeRequirement { }

public sealed class MoonPhaseRecipeRequirement : RecipeRequirement
{
    public string? MoonPhase { get; set; }
}

public sealed class PetCountRecipeRequirement : RecipeRequirement
{
    public string? PetTypeTag { get; set; }
    public int? MinCount { get; set; }
    public int? MaxCount { get; set; }
}

public sealed class RecipeKnownRequirement : RecipeRequirement
{
    public string? Recipe { get; set; }
}

public sealed class RecipeUsedRequirement : RecipeRequirement
{
    public string? Recipe { get; set; }
    public int? MaxTimesUsed { get; set; }
}

public sealed class TimeOfDayRecipeRequirement : RecipeRequirement
{
    public int? MinHour { get; set; }
    public int? MaxHour { get; set; }
}

public sealed class WeatherRequirement : RecipeRequirement
{
    public bool? ClearSky { get; set; }
}
