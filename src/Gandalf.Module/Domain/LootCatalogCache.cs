using System.Text.Json.Serialization;

namespace Gandalf.Domain;

/// <summary>
/// Per-chest-template observed cooldown duration cache, persisted to
/// <c>%LocalAppData%/Mithril/Gandalf/gandalf-loot-catalog.json</c>. Chest cooldowns
/// aren't exposed at loot time — the duration appears only on a re-loot rejection
/// screen text. Caching the (internalName → duration) mapping lets the second-ever
/// loot of any chest of that template start a correctly-sized cooldown immediately.
///
/// Global, not per-character: the duration is a property of the chest template,
/// not the player's interaction with it.
/// </summary>
public sealed class LootCatalogCache
{
    public const int Version = 1;

    public int SchemaVersion { get; set; } = Version;

    /// <summary>Map keyed by chest internal name (e.g. <c>GoblinStaticChest1</c>).</summary>
    public Dictionary<string, TimeSpan> ChestDurationByInternalName { get; set; } =
        new(StringComparer.Ordinal);
}

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase, WriteIndented = true)]
[JsonSerializable(typeof(LootCatalogCache))]
public partial class LootCatalogCacheJsonContext : JsonSerializerContext { }
