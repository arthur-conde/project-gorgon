namespace Gandalf.Domain;

/// <summary>
/// Tiny bundled defeat catalog so the Loot tab has something to render before
/// <c>mithril-calibration/defeats.json</c> ships. Olugax The Ever-Pudding from
/// the wiki sample (3-hour cooldown, sourced from out-of-band community notes).
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
            DisplayName: "Olugax the Ever-Pudding",
            RewardCooldown: TimeSpan.FromHours(3)),
    ];
}
