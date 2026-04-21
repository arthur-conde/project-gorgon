using Arwen.Domain;
using FluentAssertions;
using Xunit;

namespace Arwen.Tests;

public sealed class FavorTierTests
{
    [Theory]
    [InlineData(0, FavorTier.Neutral)]
    [InlineData(99, FavorTier.Neutral)]
    [InlineData(100, FavorTier.Comfortable)]
    [InlineData(299, FavorTier.Comfortable)]
    [InlineData(300, FavorTier.Friends)]
    [InlineData(599, FavorTier.Friends)]
    [InlineData(600, FavorTier.CloseFriends)]
    [InlineData(1200, FavorTier.BestFriends)]
    [InlineData(2000, FavorTier.LikeFamily)]
    [InlineData(3000, FavorTier.SoulMates)]
    [InlineData(5000, FavorTier.SoulMates)]
    [InlineData(-1, FavorTier.Tolerated)]
    [InlineData(-100, FavorTier.Tolerated)]
    [InlineData(-101, FavorTier.Disliked)]
    [InlineData(-600, FavorTier.Hatred)]
    [InlineData(-601, FavorTier.Despised)]
    public void TierForFavor_ReturnsCorrectTier(double favor, FavorTier expected) =>
        FavorTiers.TierForFavor(favor).Should().Be(expected);

    [Fact]
    public void FloorOf_ReturnsExpectedValues()
    {
        FavorTiers.FloorOf(FavorTier.Neutral).Should().Be(0);
        FavorTiers.FloorOf(FavorTier.Friends).Should().Be(300);
        FavorTiers.FloorOf(FavorTier.SoulMates).Should().Be(3000);
    }

    [Fact]
    public void CapOf_ReturnsExpectedValues()
    {
        FavorTiers.CapOf(FavorTier.Neutral).Should().Be(100);
        FavorTiers.CapOf(FavorTier.Friends).Should().Be(300);
        FavorTiers.CapOf(FavorTier.LikeFamily).Should().Be(1000);
        FavorTiers.CapOf(FavorTier.SoulMates).Should().BeNull();
    }

    [Theory]
    [InlineData(0, FavorTier.Neutral, 0.0)]
    [InlineData(50, FavorTier.Neutral, 0.5)]
    [InlineData(100, FavorTier.Neutral, 1.0)]
    [InlineData(300, FavorTier.Friends, 0.0)]
    [InlineData(450, FavorTier.Friends, 0.5)]
    public void ProgressInTier_CalculatesCorrectly(double favor, FavorTier tier, double expected) =>
        FavorTiers.ProgressInTier(favor, tier).Should().BeApproximately(expected, 0.001);

    [Fact]
    public void ProgressInTier_SoulMates_ReturnsNaN() =>
        double.IsNaN(FavorTiers.ProgressInTier(3500, FavorTier.SoulMates)).Should().BeTrue();

    [Fact]
    public void FavorToReachTier_FromNeutral()
    {
        // At favor 50, reaching Friends (floor 300) needs 250
        FavorTiers.FavorToReachTier(50, FavorTier.Friends).Should().Be(250);
    }

    [Fact]
    public void FavorToReachTier_AlreadyThere_ReturnsZero() =>
        FavorTiers.FavorToReachTier(500, FavorTier.Friends).Should().Be(0);

    [Fact]
    public void FavorToSoulMates_FromZero() =>
        FavorTiers.FavorToSoulMates(0).Should().Be(3000);

    [Fact]
    public void TierBreakdown_FromNeutral()
    {
        var breakdown = FavorTiers.TierBreakdown(50);
        breakdown.Should().NotBeEmpty();
        breakdown[0].Tier.Should().Be(FavorTier.Neutral);
        breakdown[0].Remaining.Should().Be(50); // 100 - 50 = 50 to get past Neutral
    }

    [Theory]
    [InlineData("Friends", true, FavorTier.Friends)]
    [InlineData("CloseFriends", true, FavorTier.CloseFriends)]
    [InlineData("SoulMates", true, FavorTier.SoulMates)]
    [InlineData("InvalidTier", false, FavorTier.Neutral)]
    [InlineData(null, false, FavorTier.Neutral)]
    public void TryParse_HandlesAllCases(string? input, bool expectedSuccess, FavorTier expectedTier)
    {
        var result = FavorTiers.TryParse(input, out var tier);
        result.Should().Be(expectedSuccess);
        tier.Should().Be(expectedTier);
    }
}
