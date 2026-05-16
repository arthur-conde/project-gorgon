using System.Collections.Generic;

namespace Mithril.Reference.Models.Npcs;

/// <summary>
/// One parsed entry from a <see cref="StoreService.CapIncreases"/> array. The raw
/// JSON form is the colon string <c>"&lt;Tier&gt;:&lt;GoldCap&gt;:&lt;keyword,keyword,…&gt;"</c>
/// (e.g. <c>"Despised:5000:Armor,Weapon,CorpseTrophy"</c>). The keyword segment may be
/// absent or empty, meaning the cap applies to any item.
/// </summary>
/// <param name="Tier">
/// The favor tier at which <em>this gold-cap row</em> unlocks (e.g. <c>"Despised"</c>,
/// <c>"Neutral"</c>). Kept verbatim — <c>"Despised"</c> is a real tier, not a sentinel.
/// Distinct from <see cref="NpcService.Favor"/>, which gates access to the Store
/// service itself; this is the per-row cap-unlock threshold.
/// </param>
/// <param name="GoldCap">
/// Maximum gold the vendor will pay for matching items at <paramref name="Tier"/>.
/// <see langword="null"/> when the raw middle segment was not an integer.
/// </param>
/// <param name="Keywords">
/// Item keyword tags this cap applies to; an empty list means the cap applies to any item.
/// </param>
public sealed record StoreCapIncrease(
    string Tier,
    int? GoldCap,
    IReadOnlyList<string> Keywords);
