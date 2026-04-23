using Gorgon.Shared.Reference;

namespace Smaug.Domain;

/// <summary>
/// Combines local Smaug observations with community-aggregated vendor rates per the
/// configured <see cref="CalibrationSource"/> mode. Two shapes (absolute price,
/// Value-ratio) share the same weighted-mean-by-sample-count blend math.
/// </summary>
public static class CommunityRatesMerger
{
    public static PriceRate? ResolveAbsolute(
        PriceRate? local,
        AbsolutePriceRatePayload? community,
        string key,
        CalibrationSource mode)
    {
        if (local is null && community is null) return null;
        return mode switch
        {
            CalibrationSource.PreferLocal => (local is { SampleCount: > 0 }) ? local : FromPayload(key, community) ?? local,
            CalibrationSource.PreferCommunity => FromPayload(key, community) ?? local,
            CalibrationSource.Blend => BlendAbsolute(local, community, key),
            _ => local,
        };
    }

    public static RatioRate? ResolveRatio(
        RatioRate? local,
        RatioPriceRatePayload? community,
        string key,
        CalibrationSource mode)
    {
        if (local is null && community is null) return null;
        return mode switch
        {
            CalibrationSource.PreferLocal => (local is { SampleCount: > 0 }) ? local : FromPayload(key, community) ?? local,
            CalibrationSource.PreferCommunity => FromPayload(key, community) ?? local,
            CalibrationSource.Blend => BlendRatio(local, community, key),
            _ => local,
        };
    }

    private static PriceRate? FromPayload(string key, AbsolutePriceRatePayload? p) =>
        p is null ? null : new PriceRate
        {
            Key = key,
            AvgPrice = p.AvgPrice,
            SampleCount = p.SampleCount,
            MinPrice = p.MinPrice,
            MaxPrice = p.MaxPrice,
        };

    private static RatioRate? FromPayload(string key, RatioPriceRatePayload? p) =>
        p is null ? null : new RatioRate
        {
            Key = key,
            AvgRatio = p.AvgRatio,
            SampleCount = p.SampleCount,
            MinRatio = p.MinRatio,
            MaxRatio = p.MaxRatio,
        };

    private static PriceRate BlendAbsolute(PriceRate? local, AbsolutePriceRatePayload? community, string key)
    {
        if (local is null && community is null) throw new ArgumentException("at least one side required");
        if (local is null) return FromPayload(key, community)!;
        if (community is null) return local;

        var total = local.SampleCount + community.SampleCount;
        return new PriceRate
        {
            Key = local.Key,
            AvgPrice = total == 0 ? 0 : (local.AvgPrice * local.SampleCount + community.AvgPrice * community.SampleCount) / total,
            SampleCount = total,
            MinPrice = Math.Min(local.MinPrice, community.MinPrice),
            MaxPrice = Math.Max(local.MaxPrice, community.MaxPrice),
        };
    }

    private static RatioRate BlendRatio(RatioRate? local, RatioPriceRatePayload? community, string key)
    {
        if (local is null && community is null) throw new ArgumentException("at least one side required");
        if (local is null) return FromPayload(key, community)!;
        if (community is null) return local;

        var total = local.SampleCount + community.SampleCount;
        return new RatioRate
        {
            Key = local.Key,
            AvgRatio = total == 0 ? 0 : (local.AvgRatio * local.SampleCount + community.AvgRatio * community.SampleCount) / total,
            SampleCount = total,
            MinRatio = Math.Min(local.MinRatio, community.MinRatio),
            MaxRatio = Math.Max(local.MaxRatio, community.MaxRatio),
        };
    }
}
