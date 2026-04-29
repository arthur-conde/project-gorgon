using System;
using System.Collections.Generic;
using Mithril.Reference.Models.Recipes;
using Mithril.Reference.Serialization.Converters;

namespace Mithril.Reference.Serialization.Discriminators;

/// <summary>
/// Maps the JSON <c>T</c> discriminator strings to concrete
/// <see cref="RecipeRequirement"/> subclasses for the
/// <c>OtherRequirements</c> field on recipes.
/// </summary>
internal static class RecipeDiscriminators
{
    public static DiscriminatedUnionConverter<RecipeRequirement, UnknownRecipeRequirement>
        BuildRequirementConverter()
        => new("T", RequirementMap);

    private static readonly IReadOnlyDictionary<string, Type> RequirementMap = new Dictionary<string, Type>
    {
        ["AlwaysFail"] = typeof(AlwaysFailRequirement),
        ["Appearance"] = typeof(AppearanceRecipeRequirement),
        ["DruidEventState"] = typeof(DruidEventStateRequirement),
        ["EntitiesNear"] = typeof(EntitiesNearRequirement),
        ["EntityPhysicalState"] = typeof(EntityPhysicalStateRecipeRequirement),
        ["EquipmentSlotEmpty"] = typeof(EquipmentSlotEmptyRecipeRequirement),
        ["FullMoon"] = typeof(FullMoonRecipeRequirement),
        ["HasEffectKeyword"] = typeof(HasEffectKeywordRecipeRequirement),
        ["HasGuildHall"] = typeof(HasGuildHallRequirement),
        ["HasHands"] = typeof(HasHandsRequirement),
        ["InGraveyard"] = typeof(InGraveyardRequirement),
        ["IsHardcore"] = typeof(IsHardcoreRequirement),
        ["IsLycanthrope"] = typeof(IsLycanthropeRequirement),
        ["MoonPhase"] = typeof(MoonPhaseRecipeRequirement),
        ["PetCount"] = typeof(PetCountRecipeRequirement),
        ["RecipeKnown"] = typeof(RecipeKnownRequirement),
        ["RecipeUsed"] = typeof(RecipeUsedRequirement),
        ["TimeOfDay"] = typeof(TimeOfDayRecipeRequirement),
        ["Weather"] = typeof(WeatherRequirement),
    };
}
