namespace Mithril.Reference.Models.Npcs;

/// <summary>
/// A favor tier's half-open favor-point interval <c>[Floor, Ceiling)</c>.
/// <see cref="Floor"/> is null for the open-bottom tier (Despised);
/// <see cref="Ceiling"/> is null for the open-top tier (SoulMates).
/// </summary>
public readonly record struct FavorTierRange(double? Floor, double? Ceiling);

/// <summary>
/// Canonical favor-point model for <see cref="FavorTier"/>. Values are the
/// Project Gorgon wiki "Total Favor" thresholds (verified). Despised is
/// unbounded below and SoulMates unbounded above — symmetric open tiers; the
/// ladder is otherwise gapless (every Ceiling is the next Floor), enforced by
/// <c>FavorScaleTests.Table_IsGaplessAndOverlapFree</c>.
/// </summary>
public static class FavorScale
{
    // Ordered low → high. Half-open [Floor, Ceiling).
    private static readonly (FavorTier Tier, FavorTierRange Range)[] Table =
    [
        (FavorTier.Despised,     new(null,  -600)),
        (FavorTier.Hated,        new(-600,  -300)),
        (FavorTier.Disliked,     new(-300,  -100)),
        (FavorTier.Tolerated,    new(-100,     0)),
        (FavorTier.Neutral,      new(   0,   100)),
        (FavorTier.Comfortable,  new( 100,   300)),
        (FavorTier.Friends,      new( 300,   600)),
        (FavorTier.CloseFriends, new( 600,  1200)),
        (FavorTier.BestFriends,  new(1200,  2000)),
        (FavorTier.LikeFamily,   new(2000,  3000)),
        (FavorTier.SoulMates,    new(3000,  null)),
    ];

    private static int IndexOf(FavorTier tier)
    {
        for (var i = 0; i < Table.Length; i++)
            if (Table[i].Tier == tier) return i;
        throw new ArgumentOutOfRangeException(nameof(tier), tier,
            "FavorScale has no range for this tier (FavorTier.Unknown is not a real tier).");
    }

    /// <summary>The favor-point interval for <paramref name="tier"/>. Throws for <see cref="FavorTier.Unknown"/>.</summary>
    public static FavorTierRange RangeOf(FavorTier tier) => Table[IndexOf(tier)].Range;

    /// <summary>The lower bound of <paramref name="tier"/>'s interval; null for Despised (unbounded below).</summary>
    public static double? FloorOf(FavorTier tier) => RangeOf(tier).Floor;

    /// <summary>The upper bound of <paramref name="tier"/>'s interval; null for SoulMates (unbounded above).</summary>
    public static double? CeilingOf(FavorTier tier) => RangeOf(tier).Ceiling;

    /// <summary>Tier width, or null when either bound is open (Despised/SoulMates).</summary>
    public static double? SpanOf(FavorTier tier)
    {
        var r = RangeOf(tier);
        return r is { Floor: { } f, Ceiling: { } c } ? c - f : null;
    }

    /// <summary>
    /// The tier a raw favor value falls in. A favor *number* always has a real
    /// tier — never returns <see cref="FavorTier.Unknown"/>; clamps to Despised
    /// at the bottom.
    /// </summary>
    public static FavorTier TierForFavor(double favor)
    {
        for (var i = Table.Length - 1; i >= 0; i--)
        {
            var floor = Table[i].Range.Floor;
            if (floor is null || favor >= floor.Value) return Table[i].Tier;
        }
        return FavorTier.Despised;
    }

    /// <summary>0.0–1.0 progress within the tier; NaN for the open tiers.</summary>
    public static double ProgressInTier(double favor, FavorTier tier)
    {
        var r = RangeOf(tier);
        if (r.Floor is not { } floor || r.Ceiling is not { } ceiling) return double.NaN;
        return Math.Clamp((favor - floor) / (ceiling - floor), 0.0, 1.0);
    }

    /// <summary>Favor points still needed to reach <paramref name="target"/>'s floor.</summary>
    public static double FavorToReachTier(double currentFavor, FavorTier target)
    {
        var floor = RangeOf(target).Floor ?? double.NegativeInfinity;
        return Math.Max(0, floor - currentFavor);
    }

    /// <summary>
    /// Remaining favor to clear each closed tier from the current position. The
    /// open-bottom tier (Despised) is skipped but does not terminate the climb;
    /// the open-top tier (SoulMates) terminates it.
    /// </summary>
    public static IReadOnlyList<(FavorTier Tier, int Remaining)> TierBreakdown(double currentFavor)
    {
        var start = IndexOf(TierForFavor(currentFavor));
        var result = new List<(FavorTier Tier, int Remaining)>();

        for (var i = start; i < Table.Length; i++)
        {
            var (tier, range) = Table[i];
            if (range.Floor is null)
            {
                // Open-bottom tier (Despised): emit no row, but advance the position
                // to its ceiling so the next tier starts from the correct floor.
                currentFavor = range.Ceiling!.Value; // Despised.Ceiling is always -600 (gapless-ladder invariant)
                continue;
            }
            if (range.Ceiling is not { } ceiling) break; // open-top (SoulMates): terminate
            var remaining = (int)Math.Max(0, ceiling - currentFavor);
            if (remaining > 0) result.Add((tier, remaining));
            currentFavor = Math.Max(currentFavor, ceiling);
        }

        return result;
    }
}
