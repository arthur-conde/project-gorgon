using Arwen.Domain;
using FluentAssertions;
using Gorgon.Shared.Reference;
using Xunit;

namespace Arwen.Tests;

public sealed class CommunityRatesMergerTests
{
    [Fact]
    public void PreferLocal_UsesLocalWhenSamples()
    {
        var local = new CategoryRate { Keyword = "k", Rate = 0.1, SampleCount = 3, MinRate = 0.09, MaxRate = 0.11 };
        var community = new CategoryRatePayload { Rate = 0.5, SampleCount = 50, MinRate = 0.4, MaxRate = 0.6 };

        var merged = CommunityRatesMerger.ResolveRate(local, community, "k", CalibrationSource.PreferLocal);

        merged!.Rate.Should().Be(0.1);
        merged.SampleCount.Should().Be(3);
    }

    [Fact]
    public void PreferLocal_FallsThroughToCommunity_WhenLocalEmpty()
    {
        var local = new CategoryRate { Keyword = "k", SampleCount = 0 };
        var community = new CategoryRatePayload { Rate = 0.5, SampleCount = 50, MinRate = 0.4, MaxRate = 0.6 };

        var merged = CommunityRatesMerger.ResolveRate(local, community, "k", CalibrationSource.PreferLocal);

        merged!.Rate.Should().Be(0.5);
        merged.SampleCount.Should().Be(50);
    }

    [Fact]
    public void PreferCommunity_UsesCommunity()
    {
        var local = new CategoryRate { Keyword = "k", Rate = 0.1, SampleCount = 5 };
        var community = new CategoryRatePayload { Rate = 0.5, SampleCount = 50, MinRate = 0.4, MaxRate = 0.6 };

        var merged = CommunityRatesMerger.ResolveRate(local, community, "k", CalibrationSource.PreferCommunity);

        merged!.Rate.Should().Be(0.5);
    }

    [Fact]
    public void Blend_WeightedMean_AndMergedMinMax()
    {
        var local = new CategoryRate { Keyword = "k", Rate = 0.10, SampleCount = 2, MinRate = 0.09, MaxRate = 0.11 };
        var community = new CategoryRatePayload { Rate = 0.20, SampleCount = 8, MinRate = 0.15, MaxRate = 0.25 };

        var merged = CommunityRatesMerger.ResolveRate(local, community, "k", CalibrationSource.Blend);

        // (0.10*2 + 0.20*8) / 10 = 0.18
        merged!.Rate.Should().BeApproximately(0.18, 0.0001);
        merged.SampleCount.Should().Be(10);
        merged.MinRate.Should().Be(0.09);
        merged.MaxRate.Should().Be(0.25);
    }

    [Fact]
    public void ReturnsNull_WhenBothNull()
    {
        CommunityRatesMerger.ResolveRate(null, null, "k", CalibrationSource.Blend).Should().BeNull();
    }

    [Fact]
    public void Blend_NullLocal_UsesCommunity()
    {
        var community = new CategoryRatePayload { Rate = 0.5, SampleCount = 50, MinRate = 0.4, MaxRate = 0.6 };

        var merged = CommunityRatesMerger.ResolveRate(null, community, "k", CalibrationSource.Blend);

        merged!.Rate.Should().Be(0.5);
        merged.SampleCount.Should().Be(50);
    }

    [Fact]
    public void Blend_NullCommunity_UsesLocal()
    {
        var local = new CategoryRate { Keyword = "k", Rate = 0.1, SampleCount = 3, MinRate = 0.09, MaxRate = 0.11 };

        var merged = CommunityRatesMerger.ResolveRate(local, null, "k", CalibrationSource.Blend);

        merged!.Rate.Should().Be(0.1);
    }
}
