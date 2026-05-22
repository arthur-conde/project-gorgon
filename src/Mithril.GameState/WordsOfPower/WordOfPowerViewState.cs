using System.Text.Json.Serialization;
using Mithril.Shared.Character;

namespace Mithril.GameState.WordsOfPower;

/// <summary>
/// Per-character WoP view-layer persistent state (#603). Maps code → last
/// observed chat-utterance timestamp ("first burn forever sets it"). Spent
/// state is monotonic — entries never leave the map under normal operation.
///
/// <para>Stored at <c>characters/{slug}/wop-spent.json</c> via
/// <see cref="PerCharacterView{T}"/>. Distinct from the discovery folder's
/// <c>wop-discovery.json</c> so the two halves of the codebook can be
/// rebuilt independently if one corrupts.</para>
/// </summary>
public sealed class WordOfPowerViewState : IVersionedState<WordOfPowerViewState>
{
    public const int Version = 1;
    public static int CurrentVersion => Version;
    public static WordOfPowerViewState Migrate(WordOfPowerViewState loaded) => loaded;

    public int SchemaVersion { get; set; } = Version;

    /// <summary>code → first-spent-at UTC timestamp.</summary>
    public Dictionary<string, DateTime> SpentAt { get; set; } = new(StringComparer.Ordinal);
}

[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    WriteIndented = true,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(WordOfPowerViewState))]
public partial class WordOfPowerViewStateJsonContext : JsonSerializerContext;
