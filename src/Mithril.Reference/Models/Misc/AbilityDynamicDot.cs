using System.Collections.Generic;

namespace Mithril.Reference.Models.Misc;

/// <summary>
/// One entry from <c>abilitydynamicdots.json</c> (top-level JSON array).
/// Defines a damage-over-time effect activated by holding particular ability
/// keywords + active skill + effect keywords; layers on top of an ability
/// at runtime.
/// </summary>
public sealed class AbilityDynamicDot
{
    public IReadOnlyList<string>? AttributesThatDelta { get; set; }
    public int DamagePerTick { get; set; }
    public string? DamageType { get; set; }
    public int Duration { get; set; }
    public int NumTicks { get; set; }
    public IReadOnlyList<string>? ReqAbilityKeywords { get; set; }
    public string? ReqActiveSkill { get; set; }
    public IReadOnlyList<string>? ReqEffectKeywords { get; set; }
    public IReadOnlyList<string>? SpecialRules { get; set; }
}
