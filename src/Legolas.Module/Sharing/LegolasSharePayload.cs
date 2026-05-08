using System.Text.Json.Serialization;
using Legolas.ViewModels;

namespace Legolas.Sharing;

/// <summary>
/// Wire format for sharing the result of a Legolas survey run. Carried inside a
/// <c>mithril://legolas/&lt;base64url&gt;</c> deep link or pasted as JSON. The payload
/// stores item display names (parser-canonical) as keys — the recipient resolves
/// icons against their own <see cref="Mithril.Shared.Reference.IReferenceDataService"/>
/// snapshot at render time, the same shape Pippin uses for its share payload.
/// </summary>
public sealed class LegolasSharePayload
{
    public const int CurrentVersion = 1;

    public int SchemaVersion { get; set; } = CurrentVersion;

    /// <summary>
    /// Sender's character name. Null when the sender opted out of sharing identity in
    /// the share dialog. The shared-report view labels itself "Shared report" in
    /// that case.
    /// </summary>
    public string? CharacterName { get; set; }

    /// <summary>UTC instant the session started — when the player anchor was confirmed.</summary>
    public DateTimeOffset StartedAt { get; set; }

    /// <summary>UTC instant the FSM hit Done.</summary>
    public DateTimeOffset CompletedAt { get; set; }

    /// <summary>Survey vs Motherlode. v1 only emits payloads in Survey mode.</summary>
    public SessionMode Mode { get; set; } = SessionMode.Survey;

    /// <summary>Number of survey nodes the player walked through this run.</summary>
    public int SurveyCount { get; set; }

    /// <summary>
    /// Items earned, keyed by item <c>InternalName</c> → total quantity. The recipient
    /// resolves display names + icons against their own
    /// <see cref="Mithril.Shared.Reference.IReferenceDataService"/> snapshot at render
    /// time, so the payload survives CDN-version drift and locale differences.
    /// Mirrors Pippin's <c>EatenFoodsByInternalName</c>.
    /// </summary>
    public Dictionary<string, int> CollectedItemsByInternalName { get; set; } =
        new(StringComparer.Ordinal);

    /// <summary>
    /// Items the sender's local catalog couldn't resolve, kept by display name as a
    /// best-effort orphan group. Recipient renders them name-only with no icon.
    /// Optional — omitted when empty.
    /// </summary>
    public Dictionary<string, int>? UnknownByName { get; set; }
}

[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    WriteIndented = true,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(LegolasSharePayload))]
public partial class LegolasShareJsonContext : JsonSerializerContext { }
