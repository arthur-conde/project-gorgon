using System.Text.Json.Serialization;

namespace Arwen.Domain;

/// <summary>
/// A single observed gift event: player gave item X to NPC Y and gained Z favor.
/// The rate is derived as <c>favorDelta / (effectivePref × itemValue × quantity)</c>,
/// where <see cref="EffectivePref"/> is the sum of all matching preferences' Pref values
/// and <see cref="Quantity"/> is the size of the gifted stack (introduced in schema v3 —
/// see <see cref="CalibrationData.Version"/>).
/// </summary>
public sealed class GiftObservation
{
    public string NpcKey { get; set; } = "";
    public string ItemInternalName { get; set; } = "";

    /// <summary>Full Keywords[] of the item at time of gifting (before any NPC filter).</summary>
    public List<string> ItemKeywords { get; set; } = [];

    /// <summary>Every NPC preference the item satisfied (all keywords of the preference present on the item).</summary>
    public List<MatchedPreference> MatchedPreferences { get; set; } = [];

    public double ItemValue { get; set; }
    public double FavorDelta { get; set; }

    /// <summary>
    /// Stack size at the moment the gift was given. Defaults to 1 — appropriate for
    /// non-stackable items and for legacy (pre-v3) observations migrated forward.
    /// Only stackable items (<c>MaxStackSize &gt; 1</c>) ever carry a value &gt; 1, and
    /// today only when a future inventory tracker is wired up to derive the count;
    /// see Arwen plans on Mithril repo for the live-tracker design.
    /// </summary>
    public int Quantity { get; set; } = 1;

    public DateTimeOffset Timestamp { get; set; }

    [JsonIgnore]
    public double EffectivePref => MatchedPreferences.Sum(p => p.Pref);

    [JsonIgnore]
    public double DerivedRate =>
        EffectivePref == 0 || ItemValue == 0 || Quantity == 0
            ? 0
            : FavorDelta / (ItemValue * EffectivePref * Quantity);

    /// <summary>Stable identifier for the preference set this item matches (sorted preference names, comma-joined).</summary>
    [JsonIgnore]
    public string Signature => BuildSignature(MatchedPreferences);

    internal static string BuildSignature(IEnumerable<MatchedPreference> prefs) =>
        string.Join(",", prefs.Select(p => p.Name).OrderBy(s => s, StringComparer.Ordinal));
}

/// <summary>One NPC preference that an item satisfied.</summary>
public sealed class MatchedPreference
{
    public string Name { get; set; } = "";
    public string Desire { get; set; } = "";
    public double Pref { get; set; }
    public List<string> Keywords { get; set; } = [];
}

/// <summary>
/// Aggregated calibration rate for a grouping key (item, signature, NPC baseline, or keyword).
/// </summary>
public sealed class CategoryRate
{
    /// <summary>Display label for the grouping (item name, signature, NPC key, or keyword).</summary>
    public string Keyword { get; set; } = "";
    public double Rate { get; set; }
    public int SampleCount { get; set; }
    public double MinRate { get; set; }
    public double MaxRate { get; set; }
}

/// <summary>
/// In-memory + export shape for calibration: raw observations plus aggregates at four
/// specificity tiers. On disk, this is split across <see cref="AggregatesData"/>
/// (<c>calibration.json</c>) and <see cref="ObservationLog"/> (<c>observations.json</c>);
/// <see cref="CalibrationData"/> is the union used by <see cref="CalibrationService"/>
/// at runtime and by <c>ExportJson</c>/<c>ImportJson</c> for the user-facing
/// "share my full calibration" workflow (a single self-contained file).
///
/// <para>
/// <b>Hand-editing the on-disk files:</b> only <c>observations.json</c> is the source
/// of truth. <c>calibration.json</c> is purely derived — <see cref="CalibrationService"/>
/// rebuilds it from observations on every load (see <c>RecomputeRates</c>), so editing
/// rates accomplishes nothing. Edit observations while the app is closed; saves are
/// tmp+rename and there's no file watcher, so a new gift landing mid-edit will persist
/// over your changes. Don't lower <see cref="Version"/> — v2→v3 migration drops every
/// stackable-item observation.
/// </para>
/// </summary>
public sealed class CalibrationData
{
    public int Version { get; set; } = 3;
    public string? ContributorNote { get; set; }
    public DateTimeOffset ExportedAt { get; set; }
    public List<GiftObservation> Observations { get; set; } = [];

    /// <summary>Most specific: keyed by "NpcKey|ItemInternalName".</summary>
    public Dictionary<string, CategoryRate> ItemRates { get; set; } = new(StringComparer.Ordinal);

    /// <summary>Keyed by "NpcKey|Signature" — generalizes across items with the same matched-preference set.</summary>
    public Dictionary<string, CategoryRate> SignatureRates { get; set; } = new(StringComparer.Ordinal);

    /// <summary>Per-NPC baseline — keyed by NpcKey.</summary>
    public Dictionary<string, CategoryRate> NpcRates { get; set; } = new(StringComparer.Ordinal);

    /// <summary>Legacy global per-keyword rate, averaged across all NPCs and items. Last-resort fallback.</summary>
    public Dictionary<string, CategoryRate> KeywordRates { get; set; } = new(StringComparer.Ordinal);
}

/// <summary>
/// On-disk shape for <c>calibration.json</c>: aggregates only, no observations.
/// Purely derived from <see cref="ObservationLog"/> — rebuilt on every load.
/// <see cref="Version"/> tracks the observation record schema (currently 3) so that
/// migration decisions key off the on-disk shape, not just the layout split. The split
/// itself (single-file → two-file) is a layout concern tracked separately on the
/// observations file.
/// </summary>
public sealed class AggregatesData
{
    public int Version { get; set; } = 3;
    public DateTimeOffset ExportedAt { get; set; }
    public Dictionary<string, CategoryRate> ItemRates { get; set; } = new(StringComparer.Ordinal);
    public Dictionary<string, CategoryRate> SignatureRates { get; set; } = new(StringComparer.Ordinal);
    public Dictionary<string, CategoryRate> NpcRates { get; set; } = new(StringComparer.Ordinal);
    public Dictionary<string, CategoryRate> KeywordRates { get; set; } = new(StringComparer.Ordinal);
}

/// <summary>
/// On-disk shape for <c>observations.json</c>: the source of truth for calibration.
/// <see cref="Version"/> matches <see cref="AggregatesData.Version"/> — the migration
/// ladder operates on this list.
/// </summary>
public sealed class ObservationLog
{
    public int Version { get; set; } = 3;
    public List<GiftObservation> Observations { get; set; } = [];
}

[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    WriteIndented = true,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(CalibrationData))]
[JsonSerializable(typeof(AggregatesData))]
[JsonSerializable(typeof(ObservationLog))]
[JsonSerializable(typeof(GiftObservation))]
[JsonSerializable(typeof(MatchedPreference))]
public partial class CalibrationJsonContext : JsonSerializerContext;
