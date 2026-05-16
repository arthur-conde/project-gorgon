using FluentAssertions;
using Smaug.Domain;
using Xunit;
using NpcService = Mithril.Shared.Reference.NpcService;
using StoreCapIncrease = Mithril.Reference.Models.Npcs.StoreCapIncrease;
using FavorTier = Mithril.Reference.Models.Npcs.FavorTier;

namespace Smaug.Tests;

public sealed class VendorCapResolverTests
{
    // #371 regression guard. Tolerated is favor -100, BELOW Neutral (favor 0), so a
    // Neutral-favor player has *more* than enough favor to qualify for a Tolerated-tier
    // cap. The prior string-based ladder had Neutral before Tolerated, which inverted that:
    // the Tolerated cap ranked ABOVE the player and ResolveMaxGold skipped it, returning
    // null. The parity/replay corpus cannot cover this (no captured Player.log lines below
    // Neutral), so this synthetic test is the correctness proof.
    [Fact]
    public void ResolveMaxGold_NeutralPlayer_AppliesLowerToleratedTierCap()
    {
        var store = new NpcService(
            Type: "Store",
            MinFavorTier: null,
            CapIncreases: new[]
            {
                new StoreCapIncrease(FavorTier.Tolerated, 500, []),
            });

        var result = VendorCapResolver.ResolveMaxGold(
            store,
            playerFavorTier: FavorTier.Neutral,
            itemKeywords: new HashSet<string>(),
            civicPrideLevel: 0);

        result.Should().Be(500);
    }

    // #385 behavioural-equivalence table — the MinFavorTier gate. These pin the
    // exact null / real-tier / junk semantics so the string→FavorTier? retype is
    // a provable no-op (green both before and after; the post-retype change is only
    // the input literal's type, never the assertion). A matching Neutral cap at 500
    // is present in every row so "not gated" surfaces as 500 and "gated" as null.

    [Fact]
    public void Gate_NullMinFavorTier_IsNotGated()
    {
        var store = new NpcService("Store", MinFavorTier: null,
            CapIncreases: new[] { new StoreCapIncrease(FavorTier.Neutral, 500, []) });

        VendorCapResolver.ResolveMaxGold(store, FavorTier.Neutral, new HashSet<string>(), 0)
            .Should().Be(500, "a null MinFavorTier means no favor gate");
    }

    [Fact]
    public void Gate_RealTierAbovePlayer_Blocks()
    {
        // Comfortable (+100) is above a Neutral player → gated, even though the
        // Neutral cap row would otherwise match.
        var store = new NpcService("Store", MinFavorTier: FavorTier.Comfortable,
            CapIncreases: new[] { new StoreCapIncrease(FavorTier.Neutral, 500, []) });

        VendorCapResolver.ResolveMaxGold(store, FavorTier.Neutral, new HashSet<string>(), 0)
            .Should().BeNull("the player's tier is below the gate");
    }

    [Fact]
    public void Gate_RealTierAtOrBelowPlayer_Admits()
    {
        var store = new NpcService("Store", MinFavorTier: FavorTier.Neutral,
            CapIncreases: new[] { new StoreCapIncrease(FavorTier.Neutral, 500, []) });

        VendorCapResolver.ResolveMaxGold(store, FavorTier.Neutral, new HashSet<string>(), 0)
            .Should().Be(500, "the player meets the gate exactly");
    }

    [Fact]
    public void Gate_UnparseableMinFavorTier_IsNotGated()
    {
        // The subtle row: Unknown (= int.MinValue, what a junk token projects to) is
        // never > any real tier, so the gate never blocks. The projection test pins
        // junk-string → Unknown; this pins Unknown → not-gated.
        var store = new NpcService("Store", MinFavorTier: FavorTier.Unknown,
            CapIncreases: new[] { new StoreCapIncrease(FavorTier.Neutral, 500, []) });

        VendorCapResolver.ResolveMaxGold(store, FavorTier.Neutral, new HashSet<string>(), 0)
            .Should().Be(500, "Unknown is the not-gated sentinel, never blocks");
    }

    // The upper side of the same boundary must stay excluded: Comfortable is favor +100,
    // ABOVE Neutral, so a Neutral-favor player must NOT receive a Comfortable-tier cap.
    [Fact]
    public void ResolveMaxGold_NeutralPlayer_ExcludesHigherComfortableTierCap()
    {
        var store = new NpcService(
            Type: "Store",
            MinFavorTier: null,
            CapIncreases: new[]
            {
                new StoreCapIncrease(FavorTier.Comfortable, 9999, []),
            });

        var result = VendorCapResolver.ResolveMaxGold(
            store,
            playerFavorTier: FavorTier.Neutral,
            itemKeywords: new HashSet<string>(),
            civicPrideLevel: 0);

        result.Should().BeNull();
    }
}
