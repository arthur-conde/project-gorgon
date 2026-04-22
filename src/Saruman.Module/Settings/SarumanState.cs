using System.Text.Json.Serialization;
using Saruman.Domain;

namespace Saruman.Settings;

public sealed class SarumanState
{
    /// <summary>Known words keyed by uppercase code.</summary>
    public Dictionary<string, KnownWord> Codebook { get; set; } = new(StringComparer.Ordinal);
}

[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    WriteIndented = true,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(SarumanState))]
public partial class SarumanJsonContext : JsonSerializerContext;
