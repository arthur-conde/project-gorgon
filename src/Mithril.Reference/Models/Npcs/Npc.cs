using System.Collections.Generic;

namespace Mithril.Reference.Models.Npcs;

/// <summary>
/// One NPC entry from <c>npcs.json</c>. Keys in the JSON envelope are the
/// NPC's internal name (e.g. <c>"NPC_Joe"</c>, <c>"Altar_Druid"</c>).
/// </summary>
public sealed class Npc
{
    public string? Name { get; set; }
    public string? Desc { get; set; }
    public string? AreaName { get; set; }
    public string? AreaFriendlyName { get; set; }

    /// <summary>Position string ("x y z" or area-specific format), missing for some altars/pedestals.</summary>
    public string? Pos { get; set; }

    public IReadOnlyList<NpcService>? Services { get; set; }
    public IReadOnlyList<NpcPreference>? Preferences { get; set; }

    /// <summary>Gift sentiment thresholds at which the NPC accepts gifts (e.g. <c>"Friends"</c>, <c>"BestFriends"</c>).</summary>
    public IReadOnlyList<string>? ItemGifts { get; set; }
}
