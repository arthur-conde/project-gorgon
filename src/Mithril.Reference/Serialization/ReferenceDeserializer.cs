using System.Collections.Generic;
using Mithril.Reference.Models.Items;
using Mithril.Reference.Models.Quests;
using Mithril.Reference.Models.Recipes;
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
}
