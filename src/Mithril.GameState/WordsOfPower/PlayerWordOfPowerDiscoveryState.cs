using System.Text.Json.Serialization;
using Mithril.Shared.Character;

namespace Mithril.GameState.WordsOfPower;

/// <summary>
/// Per-character Player.log Word-of-Power discovery ledger (#603). Persisted
/// to <c>characters/{slug}/wop-discovery.json</c> via
/// <see cref="PerCharacterView{T}"/>. Maps code → discovery record (effect name,
/// description, first-discovered timestamp). Folded from
/// <c>ProcessBook("You discovered a word of power!", …)</c> Player.log lines —
/// see <see cref="PlayerWordOfPowerDiscoveryStateService"/>.
///
/// <para>State survives across sessions so a session that starts mid-PG-run
/// (Mithril attached after the player already learned codes earlier today, or
/// codes the player learned on a different day) still shows the codebook.</para>
/// </summary>
public sealed class PlayerWordOfPowerDiscoveryStateData
    : IVersionedState<PlayerWordOfPowerDiscoveryStateData>
{
    public const int Version = 1;
    public static int CurrentVersion => Version;
    public static PlayerWordOfPowerDiscoveryStateData Migrate(PlayerWordOfPowerDiscoveryStateData loaded) => loaded;

    public int SchemaVersion { get; set; } = Version;

    /// <summary>Discovered codes keyed by uppercase code.</summary>
    public Dictionary<string, DiscoveryRecord> Discoveries { get; set; } =
        new(StringComparer.Ordinal);
}

[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    WriteIndented = true,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(PlayerWordOfPowerDiscoveryStateData))]
public partial class PlayerWordOfPowerDiscoveryStateJsonContext : JsonSerializerContext;
