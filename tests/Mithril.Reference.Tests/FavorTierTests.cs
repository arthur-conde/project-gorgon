using System;
using System.Linq;
using FluentAssertions;
using Mithril.Reference.Models.Npcs;
using Xunit;

namespace Mithril.Reference.Tests;

/// <summary>
/// #368: the narrow, correct favor-tier model used by <see cref="StoreCapIncrease.Tier"/>.
/// Locks the floor-grounded Neutral-centred ordering, the curated display strings
/// (every token is absent from <c>strings_all.json</c>), and the <see
/// cref="FavorTier.Unknown"/> sentinel that must never collide with a real rank.
/// </summary>
public class FavorTierTests
{
    private static readonly FavorTier[] RealTiers =
    [
        FavorTier.Despised, FavorTier.Hated, FavorTier.Disliked, FavorTier.Tolerated,
        FavorTier.Neutral, FavorTier.Comfortable, FavorTier.Friends, FavorTier.CloseFriends,
        FavorTier.BestFriends, FavorTier.LikeFamily, FavorTier.SoulMates,
    ];

    [Fact]
    public void Neutral_is_zero_and_order_is_floor_grounded()
    {
        ((int)FavorTier.Neutral).Should().Be(0);
        // The schism fix: Tolerated sits BELOW Neutral (real favor floor -100 < 0),
        // unlike Smaug's mis-ordered ladder (#371).
        ((int)FavorTier.Tolerated).Should().BeLessThan((int)FavorTier.Neutral);
        // Strictly ascending in declared favor order.
        RealTiers.Select(t => (int)t).Should().BeInAscendingOrder()
            .And.OnlyHaveUniqueItems();
        ((int)FavorTier.Despised).Should().Be(-4);
        ((int)FavorTier.SoulMates).Should().Be(6);
    }

    [Fact]
    public void Unknown_sorts_below_every_real_tier_and_collides_with_none()
    {
        foreach (var t in RealTiers)
            ((int)FavorTier.Unknown).Should().BeLessThan((int)t);
        // Distinct from Tolerated (-1) — the bug the sentinel-fix guards against.
        ((int)FavorTier.Unknown).Should().NotBe((int)FavorTier.Tolerated);
        ((int)FavorTier.Unknown).Should().Be(int.MinValue);
    }

    [Theory]
    [InlineData("Despised", FavorTier.Despised)]
    [InlineData("Hated", FavorTier.Hated)]          // data token is "Hated", not "Hatred"
    [InlineData("likefamily", FavorTier.LikeFamily)] // case-insensitive
    [InlineData("SoulMates", FavorTier.SoulMates)]
    public void Parse_maps_real_tokens(string token, FavorTier expected) =>
        FavorTierExtensions.Parse(token).Should().Be(expected);

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("Hatred")]   // Arwen's misspelling (#372) is NOT accepted here
    [InlineData("Nonsense")]
    [InlineData("-4")]       // numeric must not backdoor into Despised
    [InlineData("3")]
    public void Parse_unrecognised_is_Unknown(string? token) =>
        FavorTierExtensions.Parse(token).Should().Be(FavorTier.Unknown);

    [Fact]
    public void Every_real_tier_round_trips_token_and_parse()
    {
        foreach (var t in RealTiers)
            FavorTierExtensions.Parse(t.ToToken()).Should().Be(t);
    }

    [Fact]
    public void Every_tier_has_a_non_empty_display_string()
    {
        // strings_all.json has no entry for any tier (verified), so the curated map
        // is the only source — none may be blank.
        foreach (FavorTier t in Enum.GetValues<FavorTier>())
            t.DisplayName().Should().NotBeNullOrWhiteSpace();
    }

    [Theory]
    [InlineData(FavorTier.CloseFriends, "Close Friends")]
    [InlineData(FavorTier.BestFriends, "Best Friends")]
    [InlineData(FavorTier.LikeFamily, "Like Family")]
    [InlineData(FavorTier.SoulMates, "Soul Mates")]
    [InlineData(FavorTier.Despised, "Despised")]
    [InlineData(FavorTier.Friends, "Friends")]
    public void DisplayName_splits_only_the_compound_tokens(FavorTier t, string expected) =>
        t.DisplayName().Should().Be(expected);
}
