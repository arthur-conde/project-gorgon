using System;
using Mithril.Reference.Models.Recipes;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace Mithril.Reference.Serialization.Converters;

/// <summary>
/// Field-presence dispatcher for <see cref="RecipeIngredient"/> entries in
/// <c>recipes.json</c>. The JSON shape carries no explicit <c>T</c>-style
/// discriminator — instead, every ingredient object has exactly one of two
/// mutually-exclusive keys:
/// <list type="bullet">
///   <item><c>ItemCode</c> (numeric item id) → <see cref="RecipeItemIngredient"/></item>
///   <item><c>ItemKeys</c> (AND-matched keyword list) → <see cref="RecipeKeywordIngredient"/></item>
/// </list>
/// Cross-checked against the bundled <c>recipes.json</c>: 15,216/19,019
/// ingredients carry <c>ItemCode</c>, 3,803 carry <c>ItemKeys</c>, none carry
/// both or neither.
/// </summary>
/// <remarks>
/// Doesn't reuse <see cref="DiscriminatedUnionConverter{TBase, TUnknown}"/>
/// because that one expects a single string-valued discriminator field; the
/// recipe-ingredient family discriminates implicitly on field presence.
/// </remarks>
internal sealed class RecipeIngredientConverter : JsonConverter
{
    public override bool CanConvert(Type objectType) => typeof(RecipeIngredient).IsAssignableFrom(objectType);

    public override bool CanWrite => false;

    public override object? ReadJson(
        JsonReader reader,
        Type objectType,
        object? existingValue,
        JsonSerializer serializer)
    {
        if (reader.TokenType == JsonToken.Null)
            return null;

        var jObject = JObject.Load(reader);

        RecipeIngredient instance;
        if (jObject["ItemCode"] is not null)
            instance = new RecipeItemIngredient();
        else if (jObject["ItemKeys"] is not null)
            instance = new RecipeKeywordIngredient();
        else
            throw new JsonSerializationException(
                "RecipeIngredient entry has neither ItemCode nor ItemKeys — cannot pick a concrete subclass.");

        using var subReader = jObject.CreateReader();
        serializer.Populate(subReader, instance);
        return instance;
    }

    public override void WriteJson(JsonWriter writer, object? value, JsonSerializer serializer)
        => throw new NotSupportedException(
            "RecipeIngredientConverter is read-only; the reference layer doesn't serialize.");
}
