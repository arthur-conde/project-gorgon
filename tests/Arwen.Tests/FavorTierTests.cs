using Arwen.Domain;
using FluentAssertions;
using Mithril.Reference.Models.Npcs;
using Xunit;

namespace Arwen.Tests;

public sealed class FavorTierTests
{
    [Fact]
    public void TargetTierOptions_AreComfortableThroughSoulMates() =>
        FavorTiers.TargetTierOptions.Should().Equal(
            FavorTier.Comfortable, FavorTier.Friends, FavorTier.CloseFriends,
            FavorTier.BestFriends, FavorTier.LikeFamily, FavorTier.SoulMates);

    [Fact]
    public void RepresentativeFavor_UsesFloor_AndDespisedFallsBackToCeiling()
    {
        FavorTiers.RepresentativeFavor(FavorTier.Friends).Should().Be(300);
        FavorTiers.RepresentativeFavor(FavorTier.Despised).Should().Be(-600);
        FavorTiers.RepresentativeFavor(FavorTier.SoulMates).Should().Be(3000); // SoulMates returns its floor (3000), not the ceiling fallback
    }

    [Fact]
    public void Parse_UnknownToken_IsUnknown_NotNeutral() =>
        FavorTierExtensions.Parse("InvalidTier").Should().Be(FavorTier.Unknown);

    [Theory]
    [InlineData("Friends", FavorTier.Friends)]
    [InlineData("Hated", FavorTier.Hated)]
    [InlineData("SoulMates", FavorTier.SoulMates)]
    public void Parse_KnownTokens(string token, FavorTier expected) =>
        FavorTierExtensions.Parse(token).Should().Be(expected);
}
