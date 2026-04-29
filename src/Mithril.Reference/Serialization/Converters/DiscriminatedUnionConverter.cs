using System;
using System.Collections.Generic;
using Mithril.Reference.Models;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace Mithril.Reference.Serialization.Converters;

/// <summary>
/// Discriminated-union converter that replaces the typical JsonSubTypes attribute
/// flow. Reads the discriminator field from the JSON object, looks up the
/// matching concrete CLR type, and either deserializes into it or — if the
/// discriminator is unknown — instantiates a <typeparamref name="TUnknown"/>
/// sentinel carrying the unrecognized value.
/// </summary>
/// <typeparam name="TBase">Abstract base type of the polymorphic family.</typeparam>
/// <typeparam name="TUnknown">
/// Concrete sentinel subclass that implements <see cref="IUnknownDiscriminator"/>.
/// Required to have a parameterless constructor and a settable
/// <c>DiscriminatorValue</c> backing property.
/// </typeparam>
/// <remarks>
/// We don't use the third-party <c>JsonSubTypes</c> package today — fallback
/// behaviour for unknown discriminators (the CDN-drift tolerance contract) is
/// the whole point of this converter, and JsonSubTypes' default is to throw.
/// Hand-rolling the discriminator dispatch keeps the unknown path first-class.
/// </remarks>
internal sealed class DiscriminatedUnionConverter<TBase, TUnknown> : JsonConverter
    where TBase : class
    where TUnknown : TBase, IUnknownDiscriminator, new()
{
    private readonly string _discriminatorField;
    private readonly IReadOnlyDictionary<string, Type> _knownTypes;

    public DiscriminatedUnionConverter(
        string discriminatorField,
        IReadOnlyDictionary<string, Type> knownTypes)
    {
        _discriminatorField = discriminatorField;
        _knownTypes = knownTypes;
    }

    public override bool CanConvert(Type objectType) => typeof(TBase).IsAssignableFrom(objectType);

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
        var discriminator = jObject[_discriminatorField]?.Value<string>();

        if (discriminator is null)
            throw new JsonSerializationException(
                $"Discriminator field '{_discriminatorField}' missing on {typeof(TBase).Name} payload.");

        if (_knownTypes.TryGetValue(discriminator, out var concreteType))
        {
            var instance = Activator.CreateInstance(concreteType)!;
            using var subReader = jObject.CreateReader();
            serializer.Populate(subReader, instance);
            return instance;
        }

        return new TUnknown
        {
            DiscriminatorValue = discriminator,
        } is TBase result
            ? result
            : throw new InvalidOperationException(
                $"Unknown sentinel type {typeof(TUnknown).Name} doesn't derive from {typeof(TBase).Name}.");
    }

    public override void WriteJson(JsonWriter writer, object? value, JsonSerializer serializer)
        => throw new NotSupportedException(
            "DiscriminatedUnionConverter is read-only; the reference layer doesn't serialize.");
}
