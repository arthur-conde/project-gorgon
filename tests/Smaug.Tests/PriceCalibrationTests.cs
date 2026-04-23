using FluentAssertions;
using Smaug.Domain;
using Xunit;

namespace Smaug.Tests;

public sealed class PriceCalibrationTests
{
    [Fact]
    public void AbsoluteRates_AggregateByKey()
    {
        var obs = new List<PriceObservation>
        {
            Make("NPC_A", "BottleOfWater", 11, 100, FavorTierName.Neutral, 0),
            Make("NPC_A", "BottleOfWater", 11, 100, FavorTierName.Neutral, 0),
            Make("NPC_A", "BottleOfWater", 11, 110, FavorTierName.Neutral, 0),
            Make("NPC_A", "BottleOfWater", 11, 120, FavorTierName.Friends, 0),
        };

        var rates = PriceCalibrationService.BuildAbsoluteRates(obs);

        rates.Should().HaveCount(2);
        var neutralKey = PriceCalibrationService.AbsoluteKey("NPC_A", "BottleOfWater", FavorTierName.Neutral, CivicPrideBucket.Under5);
        rates[neutralKey].SampleCount.Should().Be(3);
        rates[neutralKey].AvgPrice.Should().BeApproximately((100 + 100 + 110) / 3.0, 0.01);
        rates[neutralKey].MinPrice.Should().Be(100);
        rates[neutralKey].MaxPrice.Should().Be(110);
    }

    [Fact]
    public void RatioRates_UseKeywordBucket()
    {
        var obs = new List<PriceObservation>
        {
            MakeWithBucket("NPC_A", "GloveAugment_1", 160, 160, "Augment", FavorTierName.SoulMates, 0),
            MakeWithBucket("NPC_A", "GloveAugment_2", 190, 170, "Augment", FavorTierName.SoulMates, 0),
        };

        var rates = PriceCalibrationService.BuildRatioRates(obs);

        var key = PriceCalibrationService.RatioKey("NPC_A", "Augment", FavorTierName.SoulMates, CivicPrideBucket.Under5);
        rates.Should().ContainKey(key);
        rates[key].SampleCount.Should().Be(2);
        // First obs ratio = 160/160 = 1.0; second = 170/190 ≈ 0.8947
        rates[key].AvgRatio.Should().BeApproximately((1.0 + 170.0 / 190.0) / 2.0, 0.01);
    }

    [Fact]
    public void CivicPrideBucket_Maps()
    {
        CivicPrideBucket.FromLevel(0).Should().Be(CivicPrideBucket.Under5);
        CivicPrideBucket.FromLevel(4).Should().Be(CivicPrideBucket.Under5);
        CivicPrideBucket.FromLevel(5).Should().Be(CivicPrideBucket.To14);
        CivicPrideBucket.FromLevel(14).Should().Be(CivicPrideBucket.To14);
        CivicPrideBucket.FromLevel(25).Should().Be(CivicPrideBucket.To34);
        CivicPrideBucket.FromLevel(45).Should().Be(CivicPrideBucket.AtLeast45);
        CivicPrideBucket.FromLevel(100).Should().Be(CivicPrideBucket.AtLeast45);
    }

    [Fact]
    public void FavorTierName_Rank_OrdersCorrectly()
    {
        FavorTierName.RankOf(FavorTierName.Despised).Should().Be(0);
        FavorTierName.RankOf(FavorTierName.Neutral).Should().BeGreaterThan(FavorTierName.RankOf(FavorTierName.Hated));
        FavorTierName.RankOf(FavorTierName.SoulMates).Should().Be(FavorTierName.Ordered.Count - 1);
        FavorTierName.RankOf("NotATier").Should().Be(-1);
        FavorTierName.IsAtLeast(FavorTierName.Friends, FavorTierName.Neutral).Should().BeTrue();
        FavorTierName.IsAtLeast(FavorTierName.Neutral, FavorTierName.Friends).Should().BeFalse();
    }

    private static PriceObservation Make(string npc, string internalName, long value, long paid, string tier, int cp) =>
        new()
        {
            NpcKey = npc,
            InternalName = internalName,
            BaseValue = value,
            PricePaid = paid,
            FavorTier = tier,
            CivicPrideLevel = cp,
            KeywordBucket = "",
            Timestamp = DateTimeOffset.UtcNow,
        };

    private static PriceObservation MakeWithBucket(string npc, string internalName, long value, long paid, string bucket, string tier, int cp) =>
        new()
        {
            NpcKey = npc,
            InternalName = internalName,
            BaseValue = value,
            PricePaid = paid,
            KeywordBucket = bucket,
            FavorTier = tier,
            CivicPrideLevel = cp,
            Timestamp = DateTimeOffset.UtcNow,
        };
}
