using System.Collections.Generic;

namespace Mithril.Reference.Models.Misc;

/// <summary>One entry from <c>playertitles.json</c>; keyed by <c>"Title_N"</c>.</summary>
public sealed class PlayerTitle
{
    public string? Title { get; set; }
    public string? Tooltip { get; set; }
    public IReadOnlyList<string>? Keywords { get; set; }
    public bool? AccountWide { get; set; }
    public bool? SoulWide { get; set; }
}
