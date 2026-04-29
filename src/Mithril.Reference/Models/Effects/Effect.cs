using System.Collections.Generic;

namespace Mithril.Reference.Models.Effects;

/// <summary>
/// One effect entry from <c>effects.json</c>; keyed by <c>"effect_NNNN"</c>.
/// </summary>
public sealed class Effect
{
    public string? Desc { get; set; }
    public string? DisplayMode { get; set; }
    public int IconId { get; set; }
    public IReadOnlyList<string>? Keywords { get; set; }
    public string? Name { get; set; }

    /// <summary>
    /// Int in 22958 entries, string in 28 (e.g. <c>"Permanent"</c>); coerced
    /// to string by StringOrIntStringConverter to preserve both shapes.
    /// </summary>
    public string? Duration { get; set; }

    public IReadOnlyList<string>? AbilityKeywords { get; set; }
    public int? StackingPriority { get; set; }
    public string? StackingType { get; set; }
    public string? Particle { get; set; }
    public string? SpewText { get; set; }
}
