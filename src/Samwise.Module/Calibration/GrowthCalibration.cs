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
/// A single phase-level observation: one step of the cycle (e.g. Planted → Thirsty,
/// or Growing → Ripe) with its wall-clock duration. These let partial cycles
/// contribute data and reveal which phase is responsible for any config drift.
/// </summary>
public sealed class PhaseTransitionObservation
{
    public string CropType { get; set; } = "";
    public string CharName { get; set; } = "";
    public PlotStage FromStage { get; set; }
    public PlotStage ToStage { get; set; }
    public double DurationSeconds { get; set; }
    public DateTimeOffset Timestamp { get; set; }
}

/// <summary>
/// Aggregated rate per (crop, from→to) transition. Transitions that measure
/// player reaction time (Thirsty → Growing, NeedsFertilizer → Growing) are
/// recorded raw but excluded from rate aggregation — they're not growth data.
/// </summary>
public sealed class PhaseTransitionRate
{
    public string CropType { get; set; } = "";
    public PlotStage FromStage { get; set; }
    public PlotStage ToStage { get; set; }
    public double AvgSeconds { get; set; }
    public int SampleCount { get; set; }
    public double MinSeconds { get; set; }
    public double MaxSeconds { get; set; }
}

/// <summary>
/// One observation of hitting a slot-family cap: the player attempted to plant
/// a seed for family F while already having <see cref="ObservedCap"/> plots of
/// F growing. The cap is exactly <see cref="ObservedCap"/> (since the game
/// enforces the limit, count at error time = cap).
/// </summary>
public sealed class SlotCapObservation
{
    public string CharName { get; set; } = "";
    public string Family { get; set; } = "";
    public int ObservedCap { get; set; }
    public DateTimeOffset Timestamp { get; set; }
}

/// <summary>Aggregated slot-family cap derived from observations.</summary>
public sealed class SlotCapRate
{
    public string Family { get; set; } = "";
    public int ObservedMax { get; set; }
    public int SampleCount { get; set; }
    public int? ConfigMax { get; set; }
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

    public List<PhaseTransitionObservation> PhaseTransitions { get; set; } = [];

    /// <summary>Key: "<c>CropType|FromStage→ToStage</c>".</summary>
    public Dictionary<string, PhaseTransitionRate> PhaseRates { get; set; } = new(StringComparer.Ordinal);

    public List<SlotCapObservation> SlotCapObservations { get; set; } = [];

    /// <summary>Key: slot family name.</summary>
    public Dictionary<string, SlotCapRate> SlotCapRates { get; set; } = new(StringComparer.Ordinal);
}

[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    WriteIndented = true,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(GrowthCalibrationData))]
public partial class GrowthCalibrationJsonContext : JsonSerializerContext;
