using System;
using FluentAssertions;
using Mithril.Reference.Models.Npcs;
using Smaug.Domain;
using Xunit;

namespace Smaug.Tests;

/// <summary>
/// Parity lock for #368: <see cref="StoreCapIncrease.Tier"/> became a typed
/// <see cref="FavorTier"/>, so Smaug now ranks it via the new
/// <see cref="FavorTierName.RankOf(FavorTier)"/> overload. That overload MUST be
/// byte-identical to the old raw-string path against Smaug's existing
/// <see cref="FavorTierName.Ordered"/> ladder — the (separately tracked, #371)
/// mis-order is intentionally preserved here so vendor pricing does not shift.
/// </summary>
public sealed class FavorTierNameParityTests
{
    [Fact]
    public void RankOf_typed_equals_RankOf_token_for_every_tier()
    {
        foreach (FavorTier t in Enum.GetValues<FavorTier>())
            FavorTierName.RankOf(t).Should().Be(FavorTierName.RankOf(t.ToToken()),
                $"the typed overload must delegate to the string ladder for {t}");
    }

    [Fact]
    public void Unknown_ranks_as_the_legacy_unrecognised_sentinel()
    {
        // Pre-#368 an unparseable raw cap tier string yielded RankOf == -1
        // ("sorts before Despised"); FavorTier.Unknown must reproduce that exactly.
        FavorTierName.RankOf(FavorTier.Unknown).Should().Be(-1);
        FavorTierName.RankOf(FavorTier.Unknown)
            .Should().Be(FavorTierName.RankOf("definitely-not-a-tier"));
    }

    [Fact]
    public void Known_tiers_resolve_on_the_existing_ladder()
    {
        // Spelling round-trips into Smaug's Ordered list (esp. "Hated", which Arwen
        // mis-spells — see #372): a real tier never falls through to -1.
        foreach (var name in FavorTierName.Ordered)
            FavorTierName.RankOf(FavorTierExtensions.Parse(name)).Should().BeGreaterThanOrEqualTo(0);
    }
}
