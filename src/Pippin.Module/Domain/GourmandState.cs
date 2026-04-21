using System.Text.Json.Serialization;

namespace Pippin.Domain;

/// <summary>
/// Persistence model for the Gourmand module — serialized to gourmand-state.json.
/// </summary>
public sealed class GourmandState
{
    /// <summary>Food display name → times eaten.</summary>
    public Dictionary<string, int> EatenFoods { get; set; } = new();

    /// <summary>When the last Foods Consumed report was parsed from the log.</summary>
    public DateTimeOffset? LastReportTime { get; set; }
}

[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    WriteIndented = true,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(GourmandState))]
public partial class GourmandStateJsonContext : JsonSerializerContext { }
