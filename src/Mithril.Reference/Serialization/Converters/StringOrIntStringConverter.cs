using System;
using Newtonsoft.Json;

namespace Mithril.Reference.Serialization.Converters;

/// <summary>
/// Coerces JSON values that may be either a string or an integer into a string.
/// Project Gorgon's <c>Level</c> field on quest requirements is "Friends" for
/// <c>MinFavorLevel</c> rows and an int (e.g. 25) for <c>MinSkillLevel</c> rows;
/// modelling both as <c>string</c> avoids a discriminated-union per requirement
/// type just to capture that one polymorphic field.
/// </summary>
internal sealed class StringOrIntStringConverter : JsonConverter
{
    public override bool CanConvert(Type objectType)
        => objectType == typeof(string);

    public override bool CanWrite => false;

    public override object? ReadJson(
        JsonReader reader,
        Type objectType,
        object? existingValue,
        JsonSerializer serializer)
    {
        return reader.TokenType switch
        {
            JsonToken.Null => null,
            JsonToken.String => (string?)reader.Value,
            JsonToken.Integer => reader.Value?.ToString(),
            JsonToken.Float => reader.Value?.ToString(),
            JsonToken.Boolean => reader.Value?.ToString(),
            _ => throw new JsonSerializationException(
                $"Unexpected token {reader.TokenType} when expecting string or int."),
        };
    }

    public override void WriteJson(JsonWriter writer, object? value, JsonSerializer serializer)
        => throw new NotSupportedException(
            "StringOrIntStringConverter is read-only; the reference layer doesn't serialize.");
}
