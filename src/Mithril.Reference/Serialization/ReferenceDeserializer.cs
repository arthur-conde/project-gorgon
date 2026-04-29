using System.Collections.Generic;
using Mithril.Reference.Models.Items;
using Mithril.Reference.Models.Misc;
using Mithril.Reference.Models.Npcs;
using Mithril.Reference.Models.Quests;
using Mithril.Reference.Models.Recipes;
using Mithril.Reference.Models.Sources;
using Mithril.Reference.Serialization.Converters;
using Mithril.Reference.Serialization.Discriminators;
using Newtonsoft.Json;

namespace Mithril.Reference.Serialization;

/// <summary>
/// Public Parse entry points for every BundledData JSON file. Each method
/// configures a per-file <see cref="JsonSerializerSettings"/> and returns a
/// dictionary keyed on the JSON envelope's top-level keys (e.g. <c>"quest_172"</c>).
/// </summary>
public static class ReferenceDeserializer
{
    /// <summary>
    /// Deserializes the contents of <c>quests.json</c> into a dictionary of
    /// <see cref="Quest"/> POCOs keyed by the JSON envelope's quest_id strings
    /// (e.g. <c>"quest_172"</c>).
    /// </summary>
    public static IReadOnlyDictionary<string, Quest> ParseQuests(string json)
    {
        var settings = SerializerSettings.Build();

        // Order matters: register the polymorphic dispatchers before the
        // SingleOrArrayConverter<QuestRequirement>, so when SingleOrArray reads
        // a child element via element.ToObject<QuestRequirement>(serializer),
        // the discriminator converter is already on the converters list.
        settings.Converters.Add(QuestDiscriminators.BuildRequirementConverter());
        settings.Converters.Add(QuestDiscriminators.BuildRewardConverter());

        // String-or-int handling for the <c>Level</c>/<c>Value</c> fields where
        // the JSON sometimes ships an int (e.g. <c>MinSkillLevel.Level: 25</c>)
        // and sometimes a string (<c>MinFavorLevel.Level: "Friends"</c>).
        settings.Converters.Add(new StringOrIntStringConverter());

        // Single-or-array normalisers. The list-typed properties on Quest /
        // QuestObjective absorb both shapes via these converters.
        settings.Converters.Add(new SingleOrArrayConverter<QuestRequirement>());
        settings.Converters.Add(new SingleOrArrayConverter<string>());

        var result = JsonConvert.DeserializeObject<Dictionary<string, Quest>>(json, settings);
        return result ?? new Dictionary<string, Quest>();
    }

    /// <summary>
    /// Deserializes the contents of <c>recipes.json</c> into a dictionary of
    /// <see cref="Recipe"/> POCOs keyed by the JSON envelope's recipe_id strings.
    /// </summary>
    public static IReadOnlyDictionary<string, Recipe> ParseRecipes(string json)
    {
        var settings = SerializerSettings.Build();
        settings.Converters.Add(RecipeDiscriminators.BuildRequirementConverter());
        settings.Converters.Add(new SingleOrArrayConverter<RecipeRequirement>());
        settings.Converters.Add(new SingleOrArrayConverter<string>());

        var result = JsonConvert.DeserializeObject<Dictionary<string, Recipe>>(json, settings);
        return result ?? new Dictionary<string, Recipe>();
    }

    /// <summary>
    /// Deserializes the contents of <c>items.json</c> into a dictionary of
    /// <see cref="Item"/> POCOs keyed by the JSON envelope's item_id strings
    /// (e.g. <c>"item_5010"</c>).
    /// </summary>
    public static IReadOnlyDictionary<string, Item> ParseItems(string json)
    {
        var settings = SerializerSettings.Build();
        settings.Converters.Add(new SingleOrArrayConverter<string>());

        var result = JsonConvert.DeserializeObject<Dictionary<string, Item>>(json, settings);
        return result ?? new Dictionary<string, Item>();
    }

    /// <summary>
    /// Deserializes the contents of <c>npcs.json</c> into a dictionary of
    /// <see cref="Npc"/> POCOs keyed by NPC internal name (e.g. <c>"NPC_Joe"</c>).
    /// </summary>
    public static IReadOnlyDictionary<string, Npc> ParseNpcs(string json)
    {
        var settings = SerializerSettings.Build();
        settings.Converters.Add(NpcDiscriminators.BuildServiceConverter());
        settings.Converters.Add(new SingleOrArrayConverter<string>());

        var result = JsonConvert.DeserializeObject<Dictionary<string, Npc>>(json, settings);
        return result ?? new Dictionary<string, Npc>();
    }

    /// <summary>
    /// Deserializes any of <c>sources_items.json</c>, <c>sources_recipes.json</c>,
    /// or <c>sources_abilities.json</c> into a dictionary of
    /// <see cref="SourceEnvelope"/> POCOs keyed by the JSON envelope's id strings
    /// (e.g. <c>"item_5010"</c>, <c>"recipe_172"</c>, <c>"ability_42"</c>).
    /// All three files share the same entry-shape hierarchy.
    /// </summary>
    public static IReadOnlyDictionary<string, SourceEnvelope> ParseSources(string json)
    {
        var settings = SerializerSettings.Build();
        settings.Converters.Add(SourceDiscriminators.BuildEntryConverter());
        settings.Converters.Add(new SingleOrArrayConverter<string>());

        var result = JsonConvert.DeserializeObject<Dictionary<string, SourceEnvelope>>(json, settings);
        return result ?? new Dictionary<string, SourceEnvelope>();
    }

    public static IReadOnlyDictionary<string, XpTable> ParseXpTables(string json)
        => DeserializeDictionary<XpTable>(json);

    public static IReadOnlyDictionary<string, Area> ParseAreas(string json)
        => DeserializeDictionary<Area>(json);

    public static IReadOnlyDictionary<string, AttributeDef> ParseAttributes(string json)
        => DeserializeDictionary<AttributeDef>(json);

    public static IReadOnlyDictionary<string, PlayerTitle> ParsePlayerTitles(string json)
        => DeserializeDictionary<PlayerTitle>(json);

    public static IReadOnlyDictionary<string, Lorebook> ParseLorebooks(string json)
        => DeserializeDictionary<Lorebook>(json);

    public static IReadOnlyDictionary<string, ItemUses> ParseItemUses(string json)
        => DeserializeDictionary<ItemUses>(json);

    public static LorebookInfo ParseLorebookInfo(string json)
    {
        var settings = SerializerSettings.Build();
        var result = JsonConvert.DeserializeObject<LorebookInfo>(json, settings);
        return result ?? new LorebookInfo();
    }

    public static IReadOnlyDictionary<string, IReadOnlyList<string>> ParseTsysProfiles(string json)
    {
        var settings = SerializerSettings.Build();
        var result = JsonConvert.DeserializeObject<Dictionary<string, IReadOnlyList<string>>>(json, settings);
        return result ?? new Dictionary<string, IReadOnlyList<string>>();
    }

    public static IReadOnlyList<DirectedGoal> ParseDirectedGoals(string json)
    {
        var settings = SerializerSettings.Build();
        var result = JsonConvert.DeserializeObject<List<DirectedGoal>>(json, settings);
        return result ?? new List<DirectedGoal>();
    }

    public static IReadOnlyDictionary<string, IReadOnlyList<Landmark>> ParseLandmarks(string json)
    {
        var settings = SerializerSettings.Build();
        var result = JsonConvert.DeserializeObject<Dictionary<string, IReadOnlyList<Landmark>>>(json, settings);
        return result ?? new Dictionary<string, IReadOnlyList<Landmark>>();
    }

    public static IReadOnlyDictionary<string, Skill> ParseSkills(string json)
    {
        var settings = SerializerSettings.Build();
        settings.Converters.Add(new SingleOrArrayConverter<string>());

        var result = JsonConvert.DeserializeObject<Dictionary<string, Skill>>(json, settings);
        return result ?? new Dictionary<string, Skill>();
    }

    public static IReadOnlyDictionary<string, PowerProfile> ParseTsysClientInfo(string json)
    {
        var settings = SerializerSettings.Build();
        settings.Converters.Add(new SingleOrArrayConverter<string>());

        var result = JsonConvert.DeserializeObject<Dictionary<string, PowerProfile>>(json, settings);
        return result ?? new Dictionary<string, PowerProfile>();
    }

    public static IReadOnlyDictionary<string, StorageVault> ParseStorageVaults(string json)
    {
        var settings = SerializerSettings.Build();
        settings.Converters.Add(StorageDiscriminators.BuildRequirementConverter());
        settings.Converters.Add(new SingleOrArrayConverter<StorageRequirement>());
        settings.Converters.Add(new SingleOrArrayConverter<string>());

        var result = JsonConvert.DeserializeObject<Dictionary<string, StorageVault>>(json, settings);
        return result ?? new Dictionary<string, StorageVault>();
    }

    /// <summary>
    /// Helper for the common case of <c>Dictionary&lt;string, T&gt;</c> envelopes
    /// that need only a baseline serializer + the global string list converter.
    /// </summary>
    private static IReadOnlyDictionary<string, T> DeserializeDictionary<T>(string json)
        where T : class
    {
        var settings = SerializerSettings.Build();
        settings.Converters.Add(new SingleOrArrayConverter<string>());

        var result = JsonConvert.DeserializeObject<Dictionary<string, T>>(json, settings);
        return result ?? new Dictionary<string, T>();
    }
}
