using System.Text.Json.Serialization;
using Mithril.Shared.Character;

namespace Arwen.Domain;

/// <summary>
/// Per-character Arwen state — persisted to <c>characters/{slug}/arwen.json</c>.
/// Holds the exact-favor snapshots parsed from Player.log.
/// </summary>
public sealed class ArwenFavorState : IVersionedState<ArwenFavorState>
{
    public const int Version = 1;
    public static int CurrentVersion => Version;
    public static ArwenFavorState Migrate(ArwenFavorState loaded) => loaded;

    public int SchemaVersion { get; set; } = Version;

    /// <summary>NPC key → latest exact favor snapshot.</summary>
    public Dictionary<string, NpcFavorSnapshot> Favor { get; set; } = new(StringComparer.Ordinal);

    public NpcFavorSnapshot? GetExactFavor(string npcKey)
        => Favor.TryGetValue(npcKey, out var s) ? s : null;

    public void SetExactFavor(string npcKey, double exactFavor, DateTimeOffset timestamp)
        => Favor[npcKey] = new NpcFavorSnapshot { ExactFavor = exactFavor, Timestamp = timestamp };
}

[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    WriteIndented = true,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(ArwenFavorState))]
public partial class ArwenFavorStateJsonContext : JsonSerializerContext;
