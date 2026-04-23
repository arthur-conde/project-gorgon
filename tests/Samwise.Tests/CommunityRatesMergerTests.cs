using FluentAssertions;
using Gorgon.Shared.Reference;
using Samwise.Calibration;
using Xunit;

namespace Samwise.Tests;

public sealed class CommunityRatesMergerTests
{
    // ── CropGrowthRate ──────────────────────────────────────────────────

    [Fact]
    public void ResolveCropRate_PreferLocal_UsesLocalWhenSamples()
    {
        var local = new CropGrowthRate { CropType = "Onion", AvgSeconds = 100, SampleCount = 3, MinSeconds = 90, MaxSeconds = 110 };
        var community = new GrowthRatePayload { AvgSeconds = 200, SampleCount = 50, MinSeconds = 180, MaxSeconds = 220 };

        var merged = CommunityRatesMerger.ResolveCropRate(local, community, CalibrationSource.PreferLocal);

        merged!.AvgSeconds.Should().Be(100);
        merged.SampleCount.Should().Be(3);
    }

    [Fact]
    public void ResolveCropRate_PreferLocal_FallsThroughToCommunity_WhenLocalEmpty()
    {
        var local = new CropGrowthRate { CropType = "Onion", SampleCount = 0 };
        var community = new GrowthRatePayload { AvgSeconds = 200, SampleCount = 50, MinSeconds = 180, MaxSeconds = 220 };

        var merged = CommunityRatesMerger.ResolveCropRate(local, community, CalibrationSource.PreferLocal);

        merged!.AvgSeconds.Should().Be(200);
        merged.SampleCount.Should().Be(50);
        merged.CropType.Should().Be("Onion");
    }

    [Fact]
    public void ResolveCropRate_PreferCommunity_UsesCommunity()
    {
        var local = new CropGrowthRate { CropType = "Onion", AvgSeconds = 100, SampleCount = 10 };
        var community = new GrowthRatePayload { AvgSeconds = 200, SampleCount = 50, MinSeconds = 180, MaxSeconds = 220 };

        var merged = CommunityRatesMerger.ResolveCropRate(local, community, CalibrationSource.PreferCommunity);

        merged!.AvgSeconds.Should().Be(200);
        merged.SampleCount.Should().Be(50);
    }

    [Fact]
    public void ResolveCropRate_Blend_WeightedMean()
    {
        var local = new CropGrowthRate { CropType = "Onion", AvgSeconds = 100, SampleCount = 2, MinSeconds = 90, MaxSeconds = 110 };
        var community = new GrowthRatePayload { AvgSeconds = 200, SampleCount = 8, MinSeconds = 180, MaxSeconds = 220 };

        var merged = CommunityRatesMerger.ResolveCropRate(local, community, CalibrationSource.Blend);

        // (100*2 + 200*8) / 10 = 180
        merged!.AvgSeconds.Should().BeApproximately(180, 0.01);
        merged.SampleCount.Should().Be(10);
        merged.MinSeconds.Should().Be(90);
        merged.MaxSeconds.Should().Be(220);
    }

    [Fact]
    public void ResolveCropRate_ReturnsNull_WhenBothNull()
    {
        CommunityRatesMerger.ResolveCropRate(null, null, CalibrationSource.Blend).Should().BeNull();
    }

    // ── PhaseTransitionRate ─────────────────────────────────────────────

    [Fact]
    public void ResolvePhaseRate_PreferLocal_FallsThroughToCommunity()
    {
        var local = new PhaseTransitionRate { CropType = "Onion", SampleCount = 0 };
        var community = new GrowthRatePayload { AvgSeconds = 1800, SampleCount = 8, MinSeconds = 1700, MaxSeconds = 1900 };

        var merged = CommunityRatesMerger.ResolvePhaseRate(local, community, CalibrationSource.PreferLocal);

        merged!.AvgSeconds.Should().Be(1800);
        merged.SampleCount.Should().Be(8);
    }

    [Fact]
    public void ResolvePhaseRate_Blend_WeightedMean()
    {
        var local = new PhaseTransitionRate { CropType = "Onion", AvgSeconds = 1000, SampleCount = 1, MinSeconds = 1000, MaxSeconds = 1000 };
        var community = new GrowthRatePayload { AvgSeconds = 2000, SampleCount = 3, MinSeconds = 1900, MaxSeconds = 2100 };

        var merged = CommunityRatesMerger.ResolvePhaseRate(local, community, CalibrationSource.Blend);

        // (1000*1 + 2000*3) / 4 = 1750
        merged!.AvgSeconds.Should().BeApproximately(1750, 0.01);
        merged.SampleCount.Should().Be(4);
        merged.MinSeconds.Should().Be(1000);
        merged.MaxSeconds.Should().Be(2100);
    }

    // ── SlotCapRate ─────────────────────────────────────────────────────

    [Fact]
    public void ResolveSlotCap_Blend_TakesMaxNotWeightedMean()
    {
        // Caps are ceilings — the true value is the highest cap any contributor observed.
        var local = new SlotCapRate { Family = "Lily", ObservedMax = 5, SampleCount = 1 };
        var community = new SlotCapRatePayload { ObservedMax = 8, SampleCount = 5 };

        var merged = CommunityRatesMerger.ResolveSlotCap(local, community, CalibrationSource.Blend);

        merged!.ObservedMax.Should().Be(8);
        merged.SampleCount.Should().Be(6);
    }

    [Fact]
    public void ResolveSlotCap_PreferLocal_UsesLocalWhenSamples()
    {
        var local = new SlotCapRate { Family = "Lily", ObservedMax = 5, SampleCount = 1 };
        var community = new SlotCapRatePayload { ObservedMax = 8, SampleCount = 5 };

        var merged = CommunityRatesMerger.ResolveSlotCap(local, community, CalibrationSource.PreferLocal);

        merged!.ObservedMax.Should().Be(5);
    }

    [Fact]
    public void ResolveSlotCap_PreferCommunity_UsesCommunity()
    {
        var local = new SlotCapRate { Family = "Lily", ObservedMax = 5, SampleCount = 1 };
        var community = new SlotCapRatePayload { ObservedMax = 8, SampleCount = 5 };

        var merged = CommunityRatesMerger.ResolveSlotCap(local, community, CalibrationSource.PreferCommunity);

        merged!.ObservedMax.Should().Be(8);
    }
}
