using System.Text.Json.Serialization;
using Samwise.State;

namespace Samwise.Calibration;

/// <summary>
/// A single observed growth cycle: plant → ripe with per-phase timing.
/// </summary>
public sealed class GrowthObservation
{
    public string CropType { get; set; } = "";
    public string CharName { get; set; } = "";
    public DateTimeOffset PlantedAt { get; set; }
    public DateTimeOffset RipeAt { get; set; }
    public double EffectiveSeconds { get; set; }
    public double TotalPausedSeconds { get; set; }
    public List<PhaseRecord> Phases { get; set; } = [];
    public DateTimeOffset Timestamp { get; set; }
}

/// <summary>
/// One phase within a growth cycle, recording when it was entered and how long it lasted.
/// </summary>
public sealed class PhaseRecord
{
    public PlotStage Stage { get; set; }
    public DateTimeOffset EnteredAt { get; set; }
    public double DurationSeconds { get; set; }
}

/// <summary>
/// Aggregated growth rate for a single crop, computed from observations.
/// </summary>
public sealed class CropGrowthRate
{
    public string CropType { get; set; } = "";
    public double AvgSeconds { get; set; }
    public int SampleCount { get; set; }
    public double MinSeconds { get; set; }
    public double MaxSeconds { get; set; }

    /// <summary>The growthSeconds value from crops.json, if present.</summary>
    public int? ConfigSeconds { get; set; }

    /// <summary>How far the config is off: (config - avg) / avg × 100. Positive = config is too high.</summary>
    public double? DeltaPercent { get; set; }
}

/// <summary>
/// Persisted calibration data: raw observations and derived per-crop rates.
/// </summary>
public sealed class GrowthCalibrationData
{
    public int Version { get; set; } = 1;
    public string? ContributorNote { get; set; }
    public DateTimeOffset ExportedAt { get; set; }
    public List<GrowthObservation> Observations { get; set; } = [];
    public Dictionary<string, CropGrowthRate> Rates { get; set; } = new(StringComparer.Ordinal);
}

[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    WriteIndented = true,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(GrowthCalibrationData))]
public partial class GrowthCalibrationJsonContext : JsonSerializerContext;
