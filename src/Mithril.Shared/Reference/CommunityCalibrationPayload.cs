using System.Text.Json.Serialization;

namespace Mithril.Shared.Reference;

// Wire types for the community-calibration payloads fetched from
// https://raw.githubusercontent.com/moumantai-gg/mithril-calibration/main/aggregated/{samwise|arwen}.json
// and produced by the app's Share dialogs.
//
// Field shapes must match the dictionaries persisted by each module (CropGrowthRate,
// PhaseTransitionRate, SlotCapRate, CategoryRate). Rates + sample counts only — no
// raw observations, no character names. See MEMORY upstream_schemas.md for the contract.

// ── Samwise ────────────────────────────────────────────────────────────────

public sealed class GrowthRatesPayload
{
    public int SchemaVersion { get; set; } = 1;
    public string Module { get; set; } = "samwise";
    public DateTimeOffset? ExportedAt { get; set; }
    public DateTimeOffset? AggregatedAt { get; set; }
    public string? ContributorNote { get; set; }
    public string? Submitter { get; set; }
    public bool AttributionOptOut { get; set; }

    /// <summary>Keyed by CropType.</summary>
    public Dictionary<string, GrowthRatePayload> Rates { get; set; } = new(StringComparer.Ordinal);

    /// <summary>Keyed by "CropType|FromStage→ToStage".</summary>
    public Dictionary<string, GrowthRatePayload> PhaseRates { get; set; } = new(StringComparer.Ordinal);

    /// <summary>Keyed by slot family name.</summary>
    public Dictionary<string, SlotCapRatePayload> SlotCapRates { get; set; } = new(StringComparer.Ordinal);
}

/// <summary>Shared shape for <see cref="GrowthRatesPayload.Rates"/> and <see cref="GrowthRatesPayload.PhaseRates"/>.</summary>
public sealed class GrowthRatePayload
{
    public double AvgSeconds { get; set; }
    public int SampleCount { get; set; }
    public double MinSeconds { get; set; }
    public double MaxSeconds { get; set; }
}

public sealed class SlotCapRatePayload
{
    public int ObservedMax { get; set; }
    public int SampleCount { get; set; }
}

// ── Arwen ──────────────────────────────────────────────────────────────────

public sealed class GiftRatesPayload
{
    public int SchemaVersion { get; set; } = 2;
    public string Module { get; set; } = "arwen";
    public DateTimeOffset? ExportedAt { get; set; }
    public DateTimeOffset? AggregatedAt { get; set; }
    public string? ContributorNote { get; set; }
    public string? Submitter { get; set; }
    public bool AttributionOptOut { get; set; }

    /// <summary>Keyed by "NpcKey|ItemInternalName".</summary>
    public Dictionary<string, CategoryRatePayload> ItemRates { get; set; } = new(StringComparer.Ordinal);

    /// <summary>Keyed by "NpcKey|&lt;sorted,comma-joined,preference-names&gt;".</summary>
    public Dictionary<string, CategoryRatePayload> SignatureRates { get; set; } = new(StringComparer.Ordinal);

    /// <summary>Keyed by NpcKey.</summary>
    public Dictionary<string, CategoryRatePayload> NpcRates { get; set; } = new(StringComparer.Ordinal);

    /// <summary>Keyed by keyword (legacy fallback tier).</summary>
    public Dictionary<string, CategoryRatePayload> KeywordRates { get; set; } = new(StringComparer.Ordinal);
}

public sealed class CategoryRatePayload
{
    public double Rate { get; set; }
    public int SampleCount { get; set; }
    public double MinRate { get; set; }
    public double MaxRate { get; set; }
}

// ── Smaug ──────────────────────────────────────────────────────────────────

public sealed class VendorRatesPayload
{
    public int SchemaVersion { get; set; } = 1;
    public string Module { get; set; } = "smaug";
    public DateTimeOffset? ExportedAt { get; set; }
    public DateTimeOffset? AggregatedAt { get; set; }
    public string? ContributorNote { get; set; }
    public string? Submitter { get; set; }
    public bool AttributionOptOut { get; set; }

    /// <summary>Keyed by "NpcKey|ItemInternalName|FavorTier|CivicPrideBucket". Covers fixed-Value items.</summary>
    public Dictionary<string, AbsolutePriceRatePayload> AbsoluteRates { get; set; } = new(StringComparer.Ordinal);

    /// <summary>Keyed by "NpcKey|KeywordBucket|FavorTier|CivicPrideBucket". Covers variable-Value items (augments, loot).</summary>
    public Dictionary<string, RatioPriceRatePayload> RatioRates { get; set; } = new(StringComparer.Ordinal);
}

public sealed class AbsolutePriceRatePayload
{
    public double AvgPrice { get; set; }
    public int SampleCount { get; set; }
    public long MinPrice { get; set; }
    public long MaxPrice { get; set; }
}

public sealed class RatioPriceRatePayload
{
    public double AvgRatio { get; set; }
    public int SampleCount { get; set; }
    public double MinRatio { get; set; }
    public double MaxRatio { get; set; }
}

// ── Gandalf ────────────────────────────────────────────────────────────────

/// <summary>
/// Wire shape for the Gandalf defeat-cooldown overlay
/// (<c>aggregated/gandalf.json</c> in the mithril-calibration repo). Provides
/// per-boss reward-cooldown durations the in-game log doesn't carry.
///
/// Keys are post-article-strip wisdom-line display names (e.g. <c>"Mega-Spider"</c>,
/// <c>"Olugax the Ever-Pudding"</c>, <c>"Den Mother"</c>) — the same key
/// <see cref="Gandalf.Services.LootSource"/> uses for auto-discovered
/// bosses, so calibration entries map cleanly onto learned bosses.
///
/// Read-only from the app's perspective — Gandalf's data isn't user-observed
/// (durations are folklore, not log-derived), so there's no Share dialog or
/// per-contributor submission flow. Edits go through PR to mithril-calibration.
/// </summary>
public sealed class DefeatCooldownsPayload
{
    public int SchemaVersion { get; set; } = 1;
    public string Module { get; set; } = "gandalf";
    public DateTimeOffset? ExportedAt { get; set; }
    public DateTimeOffset? AggregatedAt { get; set; }

    /// <summary>Keyed by boss display name (post-article-strip wisdom-line form).</summary>
    public Dictionary<string, DefeatCooldownEntryPayload> Defeats { get; set; } =
        new(StringComparer.Ordinal);
}

public sealed class DefeatCooldownEntryPayload
{
    /// <summary>Reward cooldown duration in seconds. Required.</summary>
    public int DurationSeconds { get; set; }

    /// <summary>Geographic / encounter region for UI grouping. Optional.</summary>
    public string? Area { get; set; }
}

[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    WriteIndented = true,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(GrowthRatesPayload))]
[JsonSerializable(typeof(GiftRatesPayload))]
[JsonSerializable(typeof(VendorRatesPayload))]
[JsonSerializable(typeof(DefeatCooldownsPayload))]
public partial class CommunityCalibrationJsonContext : JsonSerializerContext;
