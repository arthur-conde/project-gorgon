using System;
using System.Collections.Generic;
using Mithril.Reference.Models.Misc;
using Mithril.Reference.Serialization.Converters;

namespace Mithril.Reference.Serialization.Discriminators;

/// <summary>
/// Maps the JSON <c>T</c> discriminator strings to concrete
/// <see cref="StorageRequirement"/> subclasses.
/// </summary>
internal static class StorageDiscriminators
{
    public static DiscriminatedUnionConverter<StorageRequirement, UnknownStorageRequirement>
        BuildRequirementConverter()
        => new("T", RequirementMap);

    private static readonly IReadOnlyDictionary<string, Type> RequirementMap = new Dictionary<string, Type>
    {
        ["InteractionFlagSet"] = typeof(StorageInteractionFlagSetRequirement),
        ["IsLongtimeAnimal"] = typeof(StorageIsLongtimeAnimalRequirement),
        ["IsWarden"] = typeof(StorageIsWardenRequirement),
        ["QuestCompleted"] = typeof(StorageQuestCompletedRequirement),
        ["ServerRulesFlagSet"] = typeof(StorageServerRulesFlagSetRequirement),
    };
}
