using Mithril.Shared.Reference;

namespace Samwise.Calibration;

/// <summary>
/// Combines local Samwise observations with community-aggregated rates per the configured
/// <see cref="CalibrationSource"/> mode. One method per rate type — types differ in shape
/// (seconds vs cap, and SlotCap is a ceiling not a weighted mean).
/// </summary>
public static class CommunityRatesMerger
{
    public static CropGrowthRate? ResolveCropRate(
        CropGrowthRate? local,
        GrowthRatePayload? community,
        CalibrationSource mode)
    {
        if (local is null && community is null) return null;
        return mode switch
        {
            CalibrationSource.PreferLocal => (local is { SampleCount: > 0 }) ? local : AsCropRate(local?.CropType, community) ?? local,
            CalibrationSource.PreferCommunity => AsCropRate(local?.CropType, community) ?? local,
            CalibrationSource.Blend => BlendCropRate(local, community),
            _ => local,
        };
    }

    public static PhaseTransitionRate? ResolvePhaseRate(
        PhaseTransitionRate? local,
        GrowthRatePayload? community,
        CalibrationSource mode)
    {
        if (local is null && community is null) return null;
        return mode switch
        {
            CalibrationSource.PreferLocal => (local is { SampleCount: > 0 }) ? local : AsPhaseRate(local, community) ?? local,
            CalibrationSource.PreferCommunity => AsPhaseRate(local, community) ?? local,
            CalibrationSource.Blend => BlendPhaseRate(local, community),
            _ => local,
        };
    }

    public static SlotCapRate? ResolveSlotCap(
        SlotCapRate? local,
        SlotCapRatePayload? community,
        CalibrationSource mode)
    {
        if (local is null && community is null) return null;
        return mode switch
        {
            CalibrationSource.PreferLocal => (local is { SampleCount: > 0 }) ? local : AsSlotCap(local?.Family, local?.ConfigMax, community) ?? local,
            CalibrationSource.PreferCommunity => AsSlotCap(local?.Family, local?.ConfigMax, community) ?? local,
            CalibrationSource.Blend => BlendSlotCap(local, community),
            _ => local,
        };
    }

    // ── Conversions ────────────────────────────────────────────────────

    private static CropGrowthRate? AsCropRate(string? cropType, GrowthRatePayload? p) =>
        p is null ? null : new CropGrowthRate
        {
            CropType = cropType ?? "",
            AvgSeconds = p.AvgSeconds,
            SampleCount = p.SampleCount,
            MinSeconds = p.MinSeconds,
            MaxSeconds = p.MaxSeconds,
        };

    private static PhaseTransitionRate? AsPhaseRate(PhaseTransitionRate? local, GrowthRatePayload? p) =>
        p is null ? null : new PhaseTransitionRate
        {
            CropType = local?.CropType ?? "",
            FromStage = local?.FromStage ?? default,
            ToStage = local?.ToStage ?? default,
            AvgSeconds = p.AvgSeconds,
            SampleCount = p.SampleCount,
            MinSeconds = p.MinSeconds,
            MaxSeconds = p.MaxSeconds,
        };

    private static SlotCapRate? AsSlotCap(string? family, int? localConfigMax, SlotCapRatePayload? p) =>
        p is null ? null : new SlotCapRate
        {
            Family = family ?? "",
            ObservedMax = p.ObservedMax,
            SampleCount = p.SampleCount,
            ConfigMax = localConfigMax,
        };

    // ── Blend ──────────────────────────────────────────────────────────

    private static CropGrowthRate BlendCropRate(CropGrowthRate? local, GrowthRatePayload? community)
    {
        if (local is null && community is null) throw new ArgumentException("at least one side required");
        if (local is null) return AsCropRate("", community)!;
        if (community is null) return local;

        var total = local.SampleCount + community.SampleCount;
        return new CropGrowthRate
        {
            CropType = local.CropType,
            AvgSeconds = total == 0 ? 0 : (local.AvgSeconds * local.SampleCount + community.AvgSeconds * community.SampleCount) / total,
            SampleCount = total,
            MinSeconds = Math.Min(local.MinSeconds, community.MinSeconds),
            MaxSeconds = Math.Max(local.MaxSeconds, community.MaxSeconds),
            ConfigSeconds = local.ConfigSeconds,
            DeltaPercent = local.DeltaPercent,
        };
    }

    private static PhaseTransitionRate BlendPhaseRate(PhaseTransitionRate? local, GrowthRatePayload? community)
    {
        if (local is null && community is null) throw new ArgumentException("at least one side required");
        if (local is null) return AsPhaseRate(null, community)!;
        if (community is null) return local;

        var total = local.SampleCount + community.SampleCount;
        return new PhaseTransitionRate
        {
            CropType = local.CropType,
            FromStage = local.FromStage,
            ToStage = local.ToStage,
            AvgSeconds = total == 0 ? 0 : (local.AvgSeconds * local.SampleCount + community.AvgSeconds * community.SampleCount) / total,
            SampleCount = total,
            MinSeconds = Math.Min(local.MinSeconds, community.MinSeconds),
            MaxSeconds = Math.Max(local.MaxSeconds, community.MaxSeconds),
        };
    }

    /// <summary>
    /// For caps, Blend takes <b>max</b> of the observed values (a cap is a ceiling — the true
    /// value is whichever is highest) and sums sample counts.
    /// </summary>
    private static SlotCapRate BlendSlotCap(SlotCapRate? local, SlotCapRatePayload? community)
    {
        if (local is null && community is null) throw new ArgumentException("at least one side required");
        if (local is null) return AsSlotCap("", null, community)!;
        if (community is null) return local;

        return new SlotCapRate
        {
            Family = local.Family,
            ObservedMax = Math.Max(local.ObservedMax, community.ObservedMax),
            SampleCount = local.SampleCount + community.SampleCount,
            ConfigMax = local.ConfigMax,
        };
    }
}
