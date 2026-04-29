using System.Collections.Generic;

namespace Mithril.Reference.Models.Misc;

/// <summary>
/// Top-level <c>lorebookinfo.json</c> shape. The single root key is
/// <c>"Categories"</c> and the value is a dictionary of category metadata.
/// </summary>
public sealed class LorebookInfo
{
    public IReadOnlyDictionary<string, LorebookCategoryInfo>? Categories { get; set; }
}

public sealed class LorebookCategoryInfo
{
    public string? Title { get; set; }
    public string? SubTitle { get; set; }
    public string? SortTitle { get; set; }
}
