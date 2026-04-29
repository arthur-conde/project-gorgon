using System;
using System.Collections.Generic;
using Mithril.Reference.Models.Sources;
using Mithril.Reference.Serialization.Converters;

namespace Mithril.Reference.Serialization.Discriminators;

/// <summary>
/// Maps the JSON <c>type</c> discriminator strings to concrete
/// <see cref="SourceEntry"/> subclasses. Shared across all three
/// sources_*.json files — the union of every <c>type</c> value seen in
/// any of them. A type that only appears in sources_items.json is still
/// registered here; sources_recipes.json simply never instantiates it.
/// </summary>
internal static class SourceDiscriminators
{
    public static DiscriminatedUnionConverter<SourceEntry, UnknownSourceEntry>
        BuildEntryConverter()
        => new("type", EntryMap);

    private static readonly IReadOnlyDictionary<string, Type> EntryMap = new Dictionary<string, Type>
    {
        ["Angling"] = typeof(AnglingSource),
        ["Barter"] = typeof(BarterSource),
        ["CorpseButchering"] = typeof(CorpseButcheringSource),
        ["CorpseSkinning"] = typeof(CorpseSkinningSource),
        ["CorpseSkullExtraction"] = typeof(CorpseSkullExtractionSource),
        ["CraftedInteractor"] = typeof(CraftedInteractorSource),
        ["Effect"] = typeof(EffectSource),
        ["HangOut"] = typeof(HangOutSource),
        ["Item"] = typeof(ItemSource),
        ["Monster"] = typeof(MonsterSource),
        ["NpcGift"] = typeof(NpcGiftSource),
        ["Quest"] = typeof(QuestSource),
        ["QuestObjectiveMacGuffin"] = typeof(QuestObjectiveMacGuffinSource),
        ["Recipe"] = typeof(RecipeSource),
        ["ResourceInteractor"] = typeof(ResourceInteractorSource),
        ["Skill"] = typeof(SkillSource),
        ["Training"] = typeof(TrainingSource),
        ["TreasureMap"] = typeof(TreasureMapSource),
        ["Vendor"] = typeof(VendorSource),
    };
}
