namespace Gandalf.Domain;

/// <summary>
/// Tiny bundled defeat catalog so the Loot tab has something to render before
/// <c>mithril-calibration/defeats.json</c> ships. Two captured prototypes —
/// Olugax The Ever-Pudding and Megaspider — both routing through the shared
/// corpse-search signal (positive) and "too recently" rejection (negative).
/// Megaspider's actual cooldown is unknown beyond the captured
/// <c>&gt; 1h 18m</c> lower bound; 3 h is a placeholder that matches Olugax's
/// folklore until calibration lands.
/// Replace via <see cref="Services.LootSource.OverlayDefeatCatalog"/> once
/// calibration data is fetched.
/// </summary>
public static class DefeatCatalogSeed
{
    public static IReadOnlyList<DefeatCatalogEntry> Bundled { get; } =
    [
        new DefeatCatalogEntry(
            Area: "Gazluk",
            NpcInternalName: "Olugax",
            // Capital T matches the "Search Corpse of" line; the CombatInfo
            // wisdom line uses lowercase "the" but that signal is no longer
            // load-bearing.
            DisplayName: "Olugax The Ever-Pudding",
            RewardCooldown: TimeSpan.FromHours(3)),
        new DefeatCatalogEntry(
            Area: "Sun Vale",
            NpcInternalName: "Spider_Boss0",
            DisplayName: "Megaspider",
            RewardCooldown: TimeSpan.FromHours(3)),
    ];
}
