using FluentAssertions;
using Mithril.Reference.Models.Npcs;
using Xunit;

namespace Mithril.Reference.Tests;

public sealed class FavorScaleTests
{
    // The guard that would have caught Arwen's fabricated "1800": the ladder must
    // be gapless and overlap-free — every interior tier's Ceiling is the next
    // tier's Floor. Only Despised.Floor and SoulMates.Ceiling may be null (open).
    [Fact]
    public void Table_IsGaplessAndOverlapFree()
    {
        FavorTier[] ladder =
        [
            FavorTier.Despised, FavorTier.Hated, FavorTier.Disliked, FavorTier.Tolerated,
            FavorTier.Neutral, FavorTier.Comfortable, FavorTier.Friends, FavorTier.CloseFriends,
            FavorTier.BestFriends, FavorTier.LikeFamily, FavorTier.SoulMates,
        ];

        FavorScale.FloorOf(FavorTier.Despised).Should().BeNull("Despised is unbounded below");
        FavorScale.CeilingOf(FavorTier.SoulMates).Should().BeNull("SoulMates is unbounded above");

        for (var i = 0; i < ladder.Length - 1; i++)
            FavorScale.CeilingOf(ladder[i]).Should()
                .Be(FavorScale.FloorOf(ladder[i + 1]),
                    $"{ladder[i]}.Ceiling must equal {ladder[i + 1]}.Floor");
    }

    [Theory]
    [InlineData(-600.1, FavorTier.Despised)]
    [InlineData(-600, FavorTier.Hated)]
    [InlineData(-300.1, FavorTier.Hated)]
    [InlineData(-300, FavorTier.Disliked)]
    [InlineData(-100, FavorTier.Tolerated)]
    [InlineData(-0.1, FavorTier.Tolerated)]
    [InlineData(0, FavorTier.Neutral)]
    [InlineData(99.9, FavorTier.Neutral)]
    [InlineData(100, FavorTier.Comfortable)]
    [InlineData(299.9, FavorTier.Comfortable)]
    [InlineData(300, FavorTier.Friends)]
    [InlineData(600, FavorTier.CloseFriends)]
    [InlineData(1200, FavorTier.BestFriends)]
    [InlineData(2000, FavorTier.LikeFamily)]
    [InlineData(3000, FavorTier.SoulMates)]
    [InlineData(99999, FavorTier.SoulMates)]
    [InlineData(-99999, FavorTier.Despised)]
    public void TierForFavor_MatchesWikiBoundaries(double favor, FavorTier expected) =>
        FavorScale.TierForFavor(favor).Should().Be(expected);

    [Fact]
    public void TierForFavor_NeverReturnsUnknown() =>
        FavorScale.TierForFavor(-1e9).Should().Be(FavorTier.Despised);

    [Theory]
    [InlineData(0, FavorTier.Neutral, 0.0)]
    [InlineData(50, FavorTier.Neutral, 0.5)]
    [InlineData(100, FavorTier.Neutral, 1.0)]
    [InlineData(300, FavorTier.Friends, 0.0)]
    [InlineData(450, FavorTier.Friends, 0.5)]
    public void ProgressInTier_ClosedTier(double favor, FavorTier tier, double expected) =>
        FavorScale.ProgressInTier(favor, tier).Should().BeApproximately(expected, 1e-9);

    [Theory]
    [InlineData(FavorTier.SoulMates)]
    [InlineData(FavorTier.Despised)]
    public void ProgressInTier_OpenTiers_AreNaN(FavorTier tier) =>
        double.IsNaN(FavorScale.ProgressInTier(5000, tier)).Should().BeTrue();

    [Fact]
    public void CeilingOf_BothOpenTiers()
    {
        FavorScale.CeilingOf(FavorTier.Despised).Should().Be(-600);
        FavorScale.CeilingOf(FavorTier.SoulMates).Should().BeNull();
        FavorScale.FloorOf(FavorTier.SoulMates).Should().Be(3000);
    }

    [Fact]
    public void FavorToReachTier_NeededAndAlreadyThere()
    {
        FavorScale.FavorToReachTier(50, FavorTier.Friends).Should().Be(250);
        FavorScale.FavorToReachTier(500, FavorTier.Friends).Should().Be(0);
    }

    // Regression guard for the wiki correction: from a Despised-favor value the
    // breakdown must still climb every closed tier — not return empty because
    // the bottom tier is open. (Old code break-ed on any null cap.)
    [Fact]
    public void TierBreakdown_FromDespised_ClimbsAllTiers_NotEmpty()
    {
        var b = FavorScale.TierBreakdown(-1000);
        b.Should().NotBeEmpty();
        b[0].Tier.Should().Be(FavorTier.Hated, "Despised is open-bottom — first climb step is Hated");
        b.Should().Contain(x => x.Tier == FavorTier.LikeFamily);
        b.Should().NotContain(x => x.Tier == FavorTier.SoulMates, "top tier is open — terminates the climb");
    }

    [Fact]
    public void TierBreakdown_FromNeutral()
    {
        var b = FavorScale.TierBreakdown(50);
        b[0].Tier.Should().Be(FavorTier.Neutral);
        b[0].Remaining.Should().Be(50);
    }

    [Theory]
    [InlineData(FavorTier.Neutral, 100)]
    [InlineData(FavorTier.Friends, 300)]
    [InlineData(FavorTier.CloseFriends, 600)]
    public void SpanOf_ClosedTier(FavorTier tier, double expected) =>
        FavorScale.SpanOf(tier).Should().Be(expected);

    [Theory]
    [InlineData(FavorTier.Despised)]
    [InlineData(FavorTier.SoulMates)]
    public void SpanOf_OpenTiers_AreNull(FavorTier tier) =>
        FavorScale.SpanOf(tier).Should().BeNull();

    [Fact]
    public void FavorToReachTier_DespisedTarget_IsAlwaysZero() =>
        FavorScale.FavorToReachTier(500, FavorTier.Despised).Should().Be(0);

    [Fact]
    public void RangeOf_Unknown_Throws() =>
        FluentActions.Invoking(() => FavorScale.RangeOf(FavorTier.Unknown))
            .Should().Throw<ArgumentOutOfRangeException>();

    [Fact]
    public void TierBreakdown_FromDespised_FirstRemainingIsConcrete()
    {
        var b = FavorScale.TierBreakdown(-1000);
        // Despised skipped, position advances to -600; Hated ceiling -300 ⇒ 300 remaining.
        b[0].Should().Be((FavorTier.Hated, 300));
    }
}
