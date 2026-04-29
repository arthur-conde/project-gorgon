using System;
using System.Collections.Generic;
using Mithril.Reference.Models.Abilities;
using Mithril.Reference.Serialization.Converters;

namespace Mithril.Reference.Serialization.Discriminators;

/// <summary>
/// Maps the JSON <c>T</c> discriminator strings to concrete
/// <see cref="AbilitySpecialCasterRequirement"/> subclasses for the
/// <c>SpecialCasterRequirements</c> field on abilities.
/// </summary>
internal static class AbilityDiscriminators
{
    public static DiscriminatedUnionConverter<AbilitySpecialCasterRequirement, UnknownAbilitySpecialCasterRequirement>
        BuildSpecialCasterRequirementConverter()
        => new("T", RequirementMap);

    private static readonly IReadOnlyDictionary<string, Type> RequirementMap = new Dictionary<string, Type>
    {
        ["EffectKeywordUnset"] = typeof(EffectKeywordUnsetAbilityRequirement),
        ["EquippedItemKeyword"] = typeof(EquippedItemKeywordAbilityRequirement),
        ["HasEffectKeyword"] = typeof(HasEffectKeywordAbilityRequirement),
        ["HasInventorySpaceFor"] = typeof(HasInventorySpaceForRequirement),
        ["InHotspot"] = typeof(InHotspotAbilityRequirement),
        ["InMusicPerformance"] = typeof(InMusicPerformanceRequirement),
        ["InteractionFlagSet"] = typeof(InteractionFlagSetAbilityRequirement),
        ["InventoryItemKeyword"] = typeof(InventoryItemKeywordRequirement),
        ["IsDancingOnPole"] = typeof(IsDancingOnPoleRequirement),
        ["IsHardcore"] = typeof(IsHardcoreAbilityRequirement),
        ["IsLongtimeAnimal"] = typeof(IsLongtimeAnimalAbilityRequirement),
        ["IsNotGuest"] = typeof(IsNotGuestAbilityRequirement),
        ["IsNotInCombat"] = typeof(IsNotInCombatRequirement),
        ["IsNotInHotspot"] = typeof(IsNotInHotspotRequirement),
        ["IsVampire"] = typeof(IsVampireAbilityRequirement),
        ["IsVegetarian"] = typeof(IsVegetarianRequirement),
        ["IsVolunteerGuide"] = typeof(IsVolunteerGuideRequirement),
    };
}
