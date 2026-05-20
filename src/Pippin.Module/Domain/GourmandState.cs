using System.Text.Json.Serialization;
using Mithril.Shared.Character;

namespace Pippin.Domain;

/// <summary>
/// Persistence model for the Gourmand module — one file per character, written under
/// <c>characters/{slug}/pippin.json</c>.
///
/// Schema v2: the eaten-foods dictionary now keys by item <c>InternalName</c> instead of
/// display name, so persisted progress survives CDN renames and matches the cross-module
/// convention used by Celebrimbor. Foods that don't resolve to a catalog entry are kept
/// in <see cref="UnknownByName"/> so we don't lose data when the player has eaten
/// something the bundled CDN snapshot doesn't yet know about.
///
/// Migration is two-phase: <see cref="Migrate"/> moves any v1 <c>EatenFoods</c> dict into
/// <see cref="PendingLegacyByName"/> (data resolution requires the catalog, which isn't
/// guaranteed to be ready at load time). <c>GourmandStateService</c> drains
/// <see cref="PendingLegacyByName"/> once the catalog reports ready.
/// </summary>
public sealed class GourmandState : IVersionedState<GourmandState>
{
    public const int Version = 2;
    public static int CurrentVersion => Version;

    public static GourmandState Migrate(GourmandState loaded)
    {
        // v1 → v2: the legacy "eatenFoods" dict was deserialized into the back-compat
        // property below. Park it for the catalog-aware resolution step.
        if (loaded.EatenFoods is { Count: > 0 } legacy)
        {
            loaded.PendingLegacyByName ??= new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            foreach (var kv in legacy)
                loaded.PendingLegacyByName[kv.Key] = kv.Value;
        }
        loaded.EatenFoods = null;
        return loaded;
    }

    public int SchemaVersion { get; set; } = Version;

    /// <summary>
    /// Foods the player has eaten, keyed by item <c>InternalName</c>. Stable across CDN
    /// renames and localization changes — the display name is recovered at view time
    /// by joining against <c>FoodCatalog</c>.
    /// </summary>
    public Dictionary<string, int> EatenFoodsByInternalName { get; set; } =
        new(StringComparer.Ordinal);

    /// <summary>
    /// Foods that came in from a Foods Consumed report but failed to resolve to any
    /// catalog entry — kept by display name so they still surface in the UI as "Unknown"
    /// rows and don't silently disappear if the player is ahead of the bundled CDN.
    /// </summary>
    public Dictionary<string, int> UnknownByName { get; set; } =
        new(StringComparer.OrdinalIgnoreCase);

    /// <summary>When the last Foods Consumed report was parsed from the log.</summary>
    public DateTimeOffset? LastReportTime { get; set; }

    /// <summary>
    /// L1 (#550) restart-stability high-water. The driver-side
    /// <see cref="Mithril.Shared.Logging.LogSubscriptionOptions.SkipProcessedHighWater"/>
    /// filter drops every envelope whose <c>Sequence</c> is <c>&lt;=</c> this
    /// value before handler invocation, so a cold-start that re-reads the
    /// session's <c>Player.log</c> doesn't re-run the snapshot-overwrite
    /// <c>HandleReport</c> apply path on a report Mithril already processed.
    /// Pippin is the canonical demo of the high-water shape (#549): the apply
    /// is in-session idempotent (Clear + repopulate) but every cold start
    /// re-runs the full apply absent this gate; the filter introduces restart
    /// stability where none existed pre-L1. <c>null</c> = no prior session
    /// processed (first run / pre-#550 fresh state) — every envelope is
    /// delivered.
    /// </summary>
    public long? LastProcessedSequence { get; set; }

    /// <summary>
    /// Display-name-keyed entries inherited from a v1 file that the catalog hasn't yet
    /// resolved into <see cref="EatenFoodsByInternalName"/> + <see cref="UnknownByName"/>.
    /// Drained by <c>GourmandStateService.PromoteLegacyIfReady</c> once the catalog is
    /// populated. Persisted across sessions until the catalog can complete the join, so
    /// the data isn't lost if the user closes Mithril before the CDN finishes loading.
    /// </summary>
    public Dictionary<string, int>? PendingLegacyByName { get; set; }

    /// <summary>
    /// Back-compat surface for v1's <c>"eatenFoods"</c> JSON key. The setter accepts the
    /// inbound v1 dict; the getter returns null in v2 so the property is omitted from
    /// new files (per <see cref="JsonIgnoreCondition.WhenWritingNull"/>). Modules must
    /// not read or write this — use <see cref="EatenFoodsByInternalName"/> instead.
    /// </summary>
    [JsonPropertyName("eatenFoods")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public Dictionary<string, int>? EatenFoods { get; set; }
}

[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    WriteIndented = true,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(GourmandState))]
public partial class GourmandStateJsonContext : JsonSerializerContext { }
