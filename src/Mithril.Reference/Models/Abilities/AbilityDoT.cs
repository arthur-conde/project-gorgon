using System.Collections.Generic;

namespace Mithril.Reference.Models.Abilities;

/// <summary>
/// One damage-over-time entry within an ability's <see cref="AbilityPvE.DoTs"/>
/// list. Fires <see cref="NumTicks"/> times across <see cref="Duration"/>
/// seconds, dealing <see cref="DamagePerTick"/> per tick.
/// </summary>
public sealed class AbilityDoT
{
    public int DamagePerTick { get; set; }
    public string? DamageType { get; set; }
    public int Duration { get; set; }
    public int NumTicks { get; set; }
    public IReadOnlyList<string>? AttributesThatDelta { get; set; }
    public IReadOnlyList<string>? SpecialRules { get; set; }
    public string? Preface { get; set; }
    public IReadOnlyList<string>? AttributesThatMod { get; set; }
}
