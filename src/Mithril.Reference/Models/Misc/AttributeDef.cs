using System.Collections.Generic;

namespace Mithril.Reference.Models.Misc;

/// <summary>
/// One entry from <c>attributes.json</c>; resolves placeholder tokens in
/// effect-description strings (e.g. <c>"{MAX_ARMOR}"</c>) to a human-readable
/// label and formatting hint. Named <c>AttributeDef</c> to avoid collision with
/// <see cref="System.Attribute"/>.
/// </summary>
public sealed class AttributeDef
{
    public string? Label { get; set; }
    public string? DisplayType { get; set; }
    public string? DisplayRule { get; set; }
    public IReadOnlyList<int>? IconIds { get; set; }

    /// <summary>Int in 1449 entries, float in 11; modelled as double for tolerance.</summary>
    public double? DefaultValue { get; set; }

    public string? Tooltip { get; set; }
    public bool? IsHidden { get; set; }
}
