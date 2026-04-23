using System.Text.Json.Serialization;
using Gorgon.Shared.Character;
using Saruman.Domain;

namespace Saruman.Settings;

public sealed class SarumanState : IVersionedState<SarumanState>
{
    public const int Version = 1;
    public static int CurrentVersion => Version;
    public static SarumanState Migrate(SarumanState loaded) => loaded;

    public int SchemaVersion { get; set; } = Version;

    /// <summary>Known words keyed by uppercase code.</summary>
    public Dictionary<string, KnownWord> Codebook { get; set; } = new(StringComparer.Ordinal);
}

[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    WriteIndented = true,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(SarumanState))]
public partial class SarumanJsonContext : JsonSerializerContext;
