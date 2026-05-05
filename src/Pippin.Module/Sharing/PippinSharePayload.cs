using System.Text.Json.Serialization;

namespace Pippin.Sharing;

/// <summary>
/// Wire format for sharing a player's Gourmand progress. Carried inside a
/// <c>mithril://pippin/&lt;base64url&gt;</c> deep link or pasted as JSON. Keys by
/// <c>InternalName</c> so the sender's CDN snapshot doesn't have to match the
/// recipient's — the recipient rejoins display-name metadata against their own
/// <c>FoodCatalog</c> at render time.
/// </summary>
public sealed class PippinSharePayload
{
    public const int CurrentVersion = 2;

    public int SchemaVersion { get; set; } = CurrentVersion;

    /// <summary>
    /// Sender's character name. Null when the sender opted out of sharing identity in
    /// the share dialog. The shared-progress view labels itself "Shared progress" in
    /// that case.
    /// </summary>
    public string? CharacterName { get; set; }

    /// <summary>Foods eaten, keyed by item <c>InternalName</c> → times eaten.</summary>
    public Dictionary<string, int> EatenFoodsByInternalName { get; set; } =
        new(StringComparer.Ordinal);

    /// <summary>
    /// Foods the sender has eaten that didn't resolve in their catalog snapshot,
    /// kept by display name. Recipient renders them in the same "Unknown" group as
    /// the live view. Optional — omitted when empty.
    /// </summary>
    public Dictionary<string, int>? UnknownByName { get; set; }

    /// <summary>When the sender's last Foods Consumed report was parsed.</summary>
    public DateTimeOffset? LastReportTime { get; set; }
}

[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    WriteIndented = true,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(PippinSharePayload))]
public partial class PippinShareJsonContext : JsonSerializerContext { }
