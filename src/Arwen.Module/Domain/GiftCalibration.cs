using System.Text.Json.Serialization;

namespace Arwen.Domain;

/// <summary>
/// A single observed gift event: player gave item X to NPC Y and gained Z favor.
/// From this we can derive the category rate: <c>rate = favorDelta / (pref × itemValue)</c>.
/// </summary>
public sealed class GiftObservation
{
    public string NpcKey { get; set; } = "";
    public string ItemInternalName { get; set; } = "";
    public string MatchedKeyword { get; set; } = "";
    public double ItemValue { get; set; }
    public double Pref { get; set; }
    public double FavorDelta { get; set; }
    public double DerivedRate { get; set; }
    public DateTimeOffset Timestamp { get; set; }
}

/// <summary>
/// Aggregated calibration rate for a keyword category, computed from observations.
/// </summary>
public sealed class CategoryRate
{
    public string Keyword { get; set; } = "";
    public double Rate { get; set; }
    public int SampleCount { get; set; }
    public double MinRate { get; set; }
    public double MaxRate { get; set; }
}

/// <summary>
/// Persisted calibration data: raw observations and derived rates.
/// Designed for export/import to enable crowd-sourcing.
/// </summary>
public sealed class CalibrationData
{
    public int Version { get; set; } = 1;
    public string? ContributorNote { get; set; }
    public DateTimeOffset ExportedAt { get; set; }
    public List<GiftObservation> Observations { get; set; } = [];

    /// <summary>Global rates keyed by keyword category (averaged across all NPCs).</summary>
    public Dictionary<string, CategoryRate> Rates { get; set; } = new(StringComparer.Ordinal);

    /// <summary>Per-NPC rates keyed by "NpcKey|Keyword". Used when NPC-specific data exists.</summary>
    public Dictionary<string, CategoryRate> NpcRates { get; set; } = new(StringComparer.Ordinal);
}

[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    WriteIndented = true,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(CalibrationData))]
public partial class CalibrationJsonContext : JsonSerializerContext;
