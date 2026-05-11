using System;
using System.Collections.Generic;
using Mithril.Reference.Models.Items;
using Newtonsoft.Json;

namespace Mithril.Reference.Serialization.Converters;

/// <summary>
/// Reads <c>items.json</c> <c>Keywords</c> JSON arrays of strings into
/// <c>IReadOnlyList&lt;ItemKeyword&gt;</c>. Each raw string is shaped like
/// <c>"VegetarianDish=84"</c> (Tag=Quality) or just <c>"Loot"</c> (Quality
/// defaults to 0). This is one of two deliberate deviations from the
/// "property shapes match JSON exactly" invariant; see
/// <c>docs/mithril-reference-shape-quirks.md</c>.
/// </summary>
internal sealed class ItemKeywordListConverter : JsonConverter
{
    public override bool CanConvert(Type objectType)
        => typeof(IReadOnlyList<ItemKeyword>).IsAssignableFrom(objectType);

    public override bool CanWrite => false;

    public override object? ReadJson(
        JsonReader reader,
        Type objectType,
        object? existingValue,
        JsonSerializer serializer)
    {
        if (reader.TokenType == JsonToken.Null)
            return null;

        if (reader.TokenType != JsonToken.StartArray)
            throw new JsonSerializationException(
                $"Expected StartArray for Item.Keywords, got {reader.TokenType}.");

        var result = new List<ItemKeyword>();
        while (reader.Read())
        {
            if (reader.TokenType == JsonToken.EndArray)
                return result;

            if (reader.TokenType != JsonToken.String)
                throw new JsonSerializationException(
                    $"Expected String element in Item.Keywords, got {reader.TokenType}.");

            var raw = (string?)reader.Value;
            if (string.IsNullOrEmpty(raw)) continue;

            result.Add(ParseKeyword(raw!));
        }

        throw new JsonSerializationException("Unexpected end of stream reading Item.Keywords.");
    }

    /// <summary>
    /// Parses one raw keyword string. Reference behaviour comes from the slim
    /// <c>ReferenceDataService.ParseKeywords</c> in <c>Mithril.Shared</c> — port
    /// verbatim: split on the first <c>=</c>, parse the tail as int, fall back
    /// to <c>Quality = 0</c> if no <c>=</c> or the tail is non-numeric.
    /// </summary>
    public static ItemKeyword ParseKeyword(string raw)
    {
        var eq = raw.IndexOf('=');
        if (eq > 0 && int.TryParse(raw.Substring(eq + 1), out var quality))
            return new ItemKeyword(raw.Substring(0, eq), quality);
        return new ItemKeyword(raw, 0);
    }

    public override void WriteJson(JsonWriter writer, object? value, JsonSerializer serializer)
        => throw new NotSupportedException(
            "ItemKeywordListConverter is read-only; the reference layer doesn't serialize.");
}
