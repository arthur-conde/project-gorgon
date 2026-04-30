namespace Gandalf.Domain;

/// <summary>
/// Tiny bundled defeat catalog so the Loot tab has something to render before
/// <c>mithril-calibration/defeats.json</c> ships. Two captured prototypes —
/// Olugax The Ever-Pudding (scripted-event class, 3-hour cooldown sourced
/// out-of-band) and Megaspider (defeat-cooldown class). Megaspider's actual
/// cooldown is unknown beyond the captured <c>&gt; 1h 18m</c> lower bound;
/// 3 h is a placeholder until calibration lands.
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
            RewardCooldown: TimeSpan.FromHours(3),
            Class: DefeatClass.ScriptedEvent),
        new DefeatCatalogEntry(
            Area: "Sun Vale",
            NpcInternalName: "Spider_Boss0",
            DisplayName: "Megaspider",
            RewardCooldown: TimeSpan.FromHours(3),
            Class: DefeatClass.DefeatCooldown),
    ];
}
