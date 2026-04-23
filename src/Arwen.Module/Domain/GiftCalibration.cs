using System.Text.Json.Serialization;

namespace Arwen.Domain;

/// <summary>
/// A single observed gift event: player gave item X to NPC Y and gained Z favor.
/// The rate is derived as <c>favorDelta / (effectivePref × itemValue)</c>, where
/// <see cref="EffectivePref"/> is the sum of all matching preferences' Pref values.
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
    public DateTimeOffset Timestamp { get; set; }

    [JsonIgnore]
    public double EffectivePref => MatchedPreferences.Sum(p => p.Pref);

    [JsonIgnore]
    public double DerivedRate =>
        EffectivePref == 0 || ItemValue == 0 ? 0 : FavorDelta / (ItemValue * EffectivePref);

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
/// Persisted calibration data: raw observations plus aggregates at four specificity tiers.
/// </summary>
public sealed class CalibrationData
{
    public int Version { get; set; } = 2;
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

[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    WriteIndented = true,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(CalibrationData))]
[JsonSerializable(typeof(GiftObservation))]
[JsonSerializable(typeof(MatchedPreference))]
public partial class CalibrationJsonContext : JsonSerializerContext;
