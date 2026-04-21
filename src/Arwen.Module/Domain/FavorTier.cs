namespace Arwen.Domain;

public enum FavorTier
{
    Despised,
    Hatred,
    Disliked,
    Tolerated,
    Neutral,
    Comfortable,
    Friends,
    CloseFriends,
    BestFriends,
    LikeFamily,
    SoulMates,
}

public static class FavorTiers
{
    private static readonly (FavorTier Tier, int Floor, int? Cap)[] Table =
    [
        (FavorTier.Despised,     -99999, 1800),
        (FavorTier.Hatred,       -600,   300),
        (FavorTier.Disliked,     -300,   200),
        (FavorTier.Tolerated,    -100,   100),
        (FavorTier.Neutral,      0,      100),
        (FavorTier.Comfortable,  100,    200),
        (FavorTier.Friends,      300,    300),
        (FavorTier.CloseFriends, 600,    600),
        (FavorTier.BestFriends,  1200,   800),
        (FavorTier.LikeFamily,   2000,   1000),
        (FavorTier.SoulMates,    3000,   null),
    ];

    public static int FloorOf(FavorTier tier) => Table[(int)tier].Floor;

    public static int? CapOf(FavorTier tier) => Table[(int)tier].Cap;

    public static int? CeilingOf(FavorTier tier)
    {
        var cap = Table[(int)tier].Cap;
        return cap is not null ? Table[(int)tier].Floor + cap.Value : null;
    }

    public static FavorTier TierForFavor(double favor)
    {
        for (var i = Table.Length - 1; i >= 0; i--)
            if (favor >= Table[i].Floor)
                return Table[i].Tier;
        return FavorTier.Despised;
    }

    /// <summary>Returns 0.0–1.0 progress within the current tier. NaN if tier has no cap (SoulMates).</summary>
    public static double ProgressInTier(double favor, FavorTier tier)
    {
        var cap = Table[(int)tier].Cap;
        if (cap is null) return double.NaN;
        var floor = Table[(int)tier].Floor;
        var within = favor - floor;
        return Math.Clamp(within / cap.Value, 0.0, 1.0);
    }

    /// <summary>Favor points still needed to reach <paramref name="target"/> from <paramref name="currentFavor"/>.</summary>
    public static double FavorToReachTier(double currentFavor, FavorTier target)
    {
        var targetFloor = Table[(int)target].Floor;
        return Math.Max(0, targetFloor - currentFavor);
    }

    /// <summary>Favor points needed from current position to Soul Mates.</summary>
    public static double FavorToSoulMates(double currentFavor) =>
        FavorToReachTier(currentFavor, FavorTier.SoulMates);

    /// <summary>Per-tier breakdown of remaining favor from current position.</summary>
    public static IReadOnlyList<(FavorTier Tier, int Remaining)> TierBreakdown(double currentFavor)
    {
        var currentTier = TierForFavor(currentFavor);
        var result = new List<(FavorTier, int)>();

        for (var i = (int)currentTier; i < Table.Length; i++)
        {
            var (tier, floor, cap) = Table[i];
            if (cap is null) break; // SoulMates — no cap
            var ceilingFavor = floor + cap.Value;
            var remaining = (int)Math.Max(0, ceilingFavor - currentFavor);
            if (remaining > 0) result.Add((tier, remaining));
            currentFavor = Math.Max(currentFavor, ceilingFavor);
        }

        return result;
    }

    public static bool TryParse(string? name, out FavorTier tier)
    {
        if (name is not null && Enum.TryParse(name, ignoreCase: true, out tier))
            return true;
        tier = FavorTier.Neutral;
        return false;
    }

    /// <summary>Tier options for the calculator target selector (Comfortable through SoulMates).</summary>
    public static IReadOnlyList<FavorTier> TargetTierOptions { get; } =
        [FavorTier.Comfortable, FavorTier.Friends, FavorTier.CloseFriends,
         FavorTier.BestFriends, FavorTier.LikeFamily, FavorTier.SoulMates];

    public static string DisplayName(FavorTier tier) => tier switch
    {
        FavorTier.CloseFriends => "Close Friends",
        FavorTier.BestFriends => "Best Friends",
        FavorTier.LikeFamily => "Like Family",
        FavorTier.SoulMates => "Soul Mates",
        _ => tier.ToString(),
    };
}
