using System.Collections.Generic;

namespace Mithril.Reference.Models.Npcs;

/// <summary>
/// One row in an NPC's <c>Preferences</c> array — describes how the NPC feels
/// about a class of items (e.g. "Loves Fairy Wings, Pref 3.5"). The favor
/// awarded for gifting matched items is the gift sentiment baseline times
/// <see cref="Pref"/>.
/// </summary>
public sealed class NpcPreference
{
    /// <summary>Gift sentiment level (<c>Love</c>, <c>Like</c>, <c>Dislike</c>, <c>Hate</c>).</summary>
    public string? Desire { get; set; }

    /// <summary>Keyword set (AND-matched) that an item must carry to match this preference.</summary>
    public IReadOnlyList<string>? Keywords { get; set; }

    /// <summary>Multiplier on the gift sentiment baseline. Float in 205 entries, int in 521; modelled as double.</summary>
    public double Pref { get; set; }

    /// <summary>Human-readable display label (e.g. "Magic Clubs").</summary>
    public string? Name { get; set; }

    /// <summary>Minimum favor level required for this preference to apply.</summary>
    public string? Favor { get; set; }
}
