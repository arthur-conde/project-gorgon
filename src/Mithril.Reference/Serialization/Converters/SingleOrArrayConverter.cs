using System;
using System.Collections.Generic;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace Mithril.Reference.Serialization.Converters;

/// <summary>
/// Reads either a JSON array or a single JSON value into <see cref="IReadOnlyList{T}"/>.
/// Project Gorgon's data emits both shapes for the same field — e.g. a quest's
/// <c>Requirements</c> is sometimes <c>{...}</c> and sometimes <c>[{...}]</c>;
/// an objective's <c>Target</c> is sometimes <c>"Skeleton"</c> and sometimes
/// <c>["Ratkin", "Area:AreaPovus"]</c>. Apply this converter to fields where the
/// JSON producer was inconsistent.
/// </summary>
internal sealed class SingleOrArrayConverter<T> : JsonConverter
{
    public override bool CanConvert(Type objectType)
        => typeof(IReadOnlyList<T>).IsAssignableFrom(objectType);

    public override bool CanWrite => false;

    public override object? ReadJson(
        JsonReader reader,
        Type objectType,
        object? existingValue,
        JsonSerializer serializer)
    {
        if (reader.TokenType == JsonToken.Null)
            return null;

        var token = JToken.Load(reader);

        var list = new List<T>();
        Flatten(token, list, serializer);
        return list;
    }

    /// <summary>
    /// Walks <paramref name="token"/> and appends non-null values to
    /// <paramref name="accumulator"/>. Nested arrays are flattened into the
    /// parent list — Project Gorgon's <c>quests.json</c> contains rare cases
    /// where a Requirements row is itself an array, e.g.
    /// <c>"Requirements": [{...}, [{...}, {...}]]</c>. Treating nested arrays
    /// as sibling elements preserves all entries; the apparent grouping in the
    /// raw JSON appears to be a hand-editing artifact rather than load-bearing.
    /// </summary>
    private static void Flatten(JToken token, List<T> accumulator, JsonSerializer serializer)
    {
        if (token is JArray array)
        {
            foreach (var element in array)
                Flatten(element, accumulator, serializer);
            return;
        }

        if (token.Type == JTokenType.Null)
            return;

        var value = token.ToObject<T>(serializer);
        if (value is not null)
            accumulator.Add(value);
    }

    public override void WriteJson(JsonWriter writer, object? value, JsonSerializer serializer)
        => throw new NotSupportedException(
            "SingleOrArrayConverter is read-only; the reference layer doesn't serialize.");
}
