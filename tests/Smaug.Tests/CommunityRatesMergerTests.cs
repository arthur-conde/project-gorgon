using FluentAssertions;
using Mithril.Shared.Reference;
using Smaug.Domain;
using Xunit;

namespace Smaug.Tests;

public sealed class CommunityRatesMergerTests
{
    [Fact]
    public void Absolute_PreferLocal_UsesLocalWhenSamplesPresent()
    {
        var local = new PriceRate { Key = "k", AvgPrice = 100, SampleCount = 2, MinPrice = 90, MaxPrice = 110 };
        var community = new AbsolutePriceRatePayload { AvgPrice = 80, SampleCount = 100, MinPrice = 70, MaxPrice = 90 };

        var resolved = CommunityRatesMerger.ResolveAbsolute(local, community, "k", CalibrationSource.PreferLocal);
        resolved!.AvgPrice.Should().Be(100);
        resolved.SampleCount.Should().Be(2);
    }

    [Fact]
    public void Absolute_PreferLocal_FallsBackToCommunityWhenNoLocal()
    {
        var community = new AbsolutePriceRatePayload { AvgPrice = 80, SampleCount = 100, MinPrice = 70, MaxPrice = 90 };
        var resolved = CommunityRatesMerger.ResolveAbsolute(null, community, "k", CalibrationSource.PreferLocal);
        resolved!.AvgPrice.Should().Be(80);
        resolved.SampleCount.Should().Be(100);
    }

    [Fact]
    public void Absolute_Blend_UsesWeightedMean()
    {
        var local = new PriceRate { Key = "k", AvgPrice = 100, SampleCount = 1, MinPrice = 100, MaxPrice = 100 };
        var community = new AbsolutePriceRatePayload { AvgPrice = 50, SampleCount = 3, MinPrice = 40, MaxPrice = 60 };

        var resolved = CommunityRatesMerger.ResolveAbsolute(local, community, "k", CalibrationSource.Blend);
        resolved!.AvgPrice.Should().BeApproximately((100 * 1 + 50 * 3) / 4.0, 0.01);
        resolved.SampleCount.Should().Be(4);
        resolved.MinPrice.Should().Be(40);
        resolved.MaxPrice.Should().Be(100);
    }

    [Fact]
    public void Ratio_PreferCommunity_UsesCommunity()
    {
        var local = new RatioRate { Key = "k", AvgRatio = 0.9, SampleCount = 1, MinRatio = 0.9, MaxRatio = 0.9 };
        var community = new RatioPriceRatePayload { AvgRatio = 0.5, SampleCount = 50, MinRatio = 0.3, MaxRatio = 0.7 };

        var resolved = CommunityRatesMerger.ResolveRatio(local, community, "k", CalibrationSource.PreferCommunity);
        resolved!.AvgRatio.Should().Be(0.5);
        resolved.SampleCount.Should().Be(50);
    }
}
