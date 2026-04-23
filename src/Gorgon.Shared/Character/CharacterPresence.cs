using System.Text.Json.Serialization;

namespace Gorgon.Shared.Character;

/// <summary>
/// Shell-owned per-character state — persisted to <c>characters/{slug}/character.json</c>.
/// Currently holds just <see cref="LastActiveAt"/>; future cross-module character-scoped
/// fields live here rather than growing yet another JSON file.
/// </summary>
public sealed class CharacterPresence : IVersionedState<CharacterPresence>
{
    public const int Version = 1;
    public static int CurrentVersion => Version;
    public static CharacterPresence Migrate(CharacterPresence loaded) => loaded;

    public int SchemaVersion { get; set; } = Version;

    /// <summary>When this character was last the active character. Stamped on switch-away or graceful shutdown.</summary>
    public DateTimeOffset? LastActiveAt { get; set; }
}

[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    WriteIndented = true,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(CharacterPresence))]
public partial class CharacterPresenceJsonContext : JsonSerializerContext { }
