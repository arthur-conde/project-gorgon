using System.Collections.Generic;

namespace Mithril.Reference.Models.Abilities;

/// <summary>
/// One entry in an ability's <see cref="AbilityPvE.SpecialValues"/> array —
/// a numeric value with a label and suffix used for tooltip display.
/// </summary>
public sealed class AbilitySpecialValue
{
    public string? Label { get; set; }
    public string? Suffix { get; set; }

    /// <summary>Int in 1776 entries, float in 33; modelled as double for tolerance.</summary>
    public double Value { get; set; }

    public IReadOnlyList<string>? AttributesThatDelta { get; set; }
    public bool? SkipIfZero { get; set; }
    public IReadOnlyList<string>? AttributesThatMod { get; set; }
    public string? DisplayType { get; set; }
    public IReadOnlyList<string>? AttributesThatDeltaBase { get; set; }
    public string? SkipIfThisAttributeIsZero { get; set; }
}
