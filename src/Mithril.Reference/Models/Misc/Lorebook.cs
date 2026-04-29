using System.Collections.Generic;

namespace Mithril.Reference.Models.Misc;

/// <summary>One entry from <c>lorebooks.json</c>; keyed by <c>"Book_N"</c>.</summary>
public sealed class Lorebook
{
    public string? Category { get; set; }
    public string? InternalName { get; set; }
    public bool IsClientLocal { get; set; }
    public IReadOnlyList<string>? Keywords { get; set; }
    public string? Title { get; set; }
    public string? Visibility { get; set; }
    public string? Text { get; set; }
    public string? LocationHint { get; set; }
}
