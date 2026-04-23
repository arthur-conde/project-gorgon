namespace Smaug.Domain;

/// <summary>
/// NPC favor tier names as emitted by <c>ProcessVendorScreen</c> in the Player.log.
/// The game logs these as PascalCase strings; we keep them opaque.
/// </summary>
public static class FavorTierName
{
    public const string Despised = "Despised";
    public const string Hated = "Hated";
    public const string Disliked = "Disliked";
    public const string Neutral = "Neutral";
    public const string Tolerated = "Tolerated";
    public const string Comfortable = "Comfortable";
    public const string Friends = "Friends";
    public const string CloseFriends = "CloseFriends";
    public const string BestFriends = "BestFriends";
    public const string LikeFamily = "LikeFamily";
    public const string SoulMates = "SoulMates";

    /// <summary>Ascending order from lowest (Despised) to highest (SoulMates).</summary>
    public static readonly IReadOnlyList<string> Ordered =
    [
        Despised, Hated, Disliked, Neutral, Tolerated, Comfortable,
        Friends, CloseFriends, BestFriends, LikeFamily, SoulMates,
    ];

    /// <summary>Rank for ordering. Unknown tiers sort before Despised.</summary>
    public static int RankOf(string? tier)
    {
        if (string.IsNullOrEmpty(tier)) return -1;
        for (var i = 0; i < Ordered.Count; i++)
            if (string.Equals(Ordered[i], tier, StringComparison.Ordinal)) return i;
        return -1;
    }

    public static bool IsAtLeast(string? current, string? required) =>
        RankOf(current) >= RankOf(required);
}
