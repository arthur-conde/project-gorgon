using Newtonsoft.Json;

namespace Mithril.Reference.Serialization;

/// <summary>
/// Centralised <see cref="JsonSerializerSettings"/> factory. Every Parse entry
/// point starts from <see cref="Build"/> and adds file-specific converters
/// (e.g. discriminator dispatchers for that file's polymorphic families).
/// </summary>
internal static class SerializerSettings
{
    /// <summary>
    /// Lenient base settings for reading hand-tended Project Gorgon data:
    /// missing properties are ignored (CDN may add fields ahead of POCOs),
    /// null values do not overwrite defaults, and the contract resolver is
    /// the literal-match resolver from <see cref="BundledDataContractResolver"/>.
    /// </summary>
    public static JsonSerializerSettings Build()
        => new()
        {
            ContractResolver = new BundledDataContractResolver(),
            MissingMemberHandling = MissingMemberHandling.Ignore,
            NullValueHandling = NullValueHandling.Ignore,
            DefaultValueHandling = DefaultValueHandling.Populate,
            FloatParseHandling = FloatParseHandling.Double,
        };
}
