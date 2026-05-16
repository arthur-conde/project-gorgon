using Mithril.Reference.Models.Npcs;

namespace Arwen.Domain;

/// <summary>
/// Arwen-local favor-UI adapter. The favor model itself is canonical
/// (<see cref="FavorTier"/> + <see cref="FavorScale"/> in Mithril.Reference);
/// this holds only Arwen view concerns: the calculator's target picker list and
/// the "estimate a favor value from a known tier" heuristic used when the exact
/// favor is unknown.
/// </summary>
public static class FavorTiers
{
    /// <summary>Calculator target options (Comfortable → Soul Mates). Bound by
    /// FavorCalculatorTab.xaml via <c>x:Static</c>.</summary>
    public static IReadOnlyList<FavorTier> TargetTierOptions { get; } =
        [FavorTier.Comfortable, FavorTier.Friends, FavorTier.CloseFriends,
         FavorTier.BestFriends, FavorTier.LikeFamily, FavorTier.SoulMates];

    /// <summary>
    /// A finite favor value standing in for a tier when only the tier is known
    /// (not the exact favor): the tier floor, or for open-bottom Despised its
    /// ceiling (−600 — wiki-grounded, vs the old fabricated −99999).
    /// </summary>
    public static double RepresentativeFavor(FavorTier tier) =>
        FavorScale.FloorOf(tier) ?? FavorScale.CeilingOf(tier) ?? 0;
}
