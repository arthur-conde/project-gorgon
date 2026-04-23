using Gorgon.Shared.Reference;

namespace Arwen.Domain;

/// <summary>
/// Combines local Arwen observations with community-aggregated rates per the configured
/// <see cref="CalibrationSource"/> mode. All four Arwen tiers use <see cref="CategoryRate"/>,
/// so one method handles them all.
/// </summary>
public static class CommunityRatesMerger
{
    public static CategoryRate? ResolveRate(
        CategoryRate? local,
        CategoryRatePayload? community,
        string keyForDisplay,
        CalibrationSource mode)
    {
        if (local is null && community is null) return null;
        return mode switch
        {
            CalibrationSource.PreferLocal => (local is { SampleCount: > 0 }) ? local : AsCategoryRate(keyForDisplay, community) ?? local,
            CalibrationSource.PreferCommunity => AsCategoryRate(keyForDisplay, community) ?? local,
            CalibrationSource.Blend => Blend(local, community, keyForDisplay),
            _ => local,
        };
    }

    private static CategoryRate? AsCategoryRate(string keyForDisplay, CategoryRatePayload? p) =>
        p is null ? null : new CategoryRate
        {
            Keyword = keyForDisplay,
            Rate = p.Rate,
            SampleCount = p.SampleCount,
            MinRate = p.MinRate,
            MaxRate = p.MaxRate,
        };

    private static CategoryRate Blend(CategoryRate? local, CategoryRatePayload? community, string keyForDisplay)
    {
        if (local is null && community is null) throw new ArgumentException("at least one side required");
        if (local is null) return AsCategoryRate(keyForDisplay, community)!;
        if (community is null) return local;

        var total = local.SampleCount + community.SampleCount;
        return new CategoryRate
        {
            Keyword = local.Keyword,
            Rate = total == 0 ? 0 : (local.Rate * local.SampleCount + community.Rate * community.SampleCount) / total,
            SampleCount = total,
            MinRate = Math.Min(local.MinRate, community.MinRate),
            MaxRate = Math.Max(local.MaxRate, community.MaxRate),
        };
    }
}
