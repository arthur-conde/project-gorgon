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
