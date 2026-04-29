using System.Collections.Generic;

namespace Mithril.Reference.Models.Misc;

/// <summary>
/// One entry from <c>abilitydynamicspecialvalues.json</c> (top-level JSON
/// array). Layers a special-value tooltip block onto an ability at runtime
/// when the ability/effect-keyword preconditions are met.
/// </summary>
public sealed class AbilityDynamicSpecialValue
{
    public string? Label { get; set; }
    public IReadOnlyList<string>? ReqAbilityKeywords { get; set; }
    public IReadOnlyList<string>? ReqEffectKeywords { get; set; }
    public bool? SkipIfZero { get; set; }
    public string? Suffix { get; set; }

    /// <summary>Int in some entries, float in others; modelled as double.</summary>
    public double Value { get; set; }

    public IReadOnlyList<string>? AttributesThatDelta { get; set; }
}
