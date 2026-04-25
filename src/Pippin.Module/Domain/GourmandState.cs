using System.Text.Json.Serialization;
using Mithril.Shared.Character;

namespace Pippin.Domain;

/// <summary>
/// Persistence model for the Gourmand module — one file per character, written under
/// <c>characters/{slug}/pippin.json</c>.
/// </summary>
public sealed class GourmandState : IVersionedState<GourmandState>
{
    public const int Version = 1;
    public static int CurrentVersion => Version;
    public static GourmandState Migrate(GourmandState loaded) => loaded;

    public int SchemaVersion { get; set; } = Version;

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
