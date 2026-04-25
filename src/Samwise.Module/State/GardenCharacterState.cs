using System.Text.Json.Serialization;
using Mithril.Shared.Character;

namespace Samwise.State;

/// <summary>
/// Per-character Samwise state — persisted to <c>characters/{slug}/samwise.json</c>.
/// Each character's plot dict is in its own file.
/// </summary>
public sealed class GardenCharacterState : IVersionedState<GardenCharacterState>
{
    public const int Version = 1;
    public static int CurrentVersion => Version;
    public static GardenCharacterState Migrate(GardenCharacterState loaded) => loaded;

    public int SchemaVersion { get; set; } = Version;

    public Dictionary<string, PersistedPlot> Plots { get; set; } = new(StringComparer.Ordinal);
}

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase, WriteIndented = true)]
[JsonSerializable(typeof(GardenCharacterState))]
public partial class GardenCharacterStateJsonContext : JsonSerializerContext { }
