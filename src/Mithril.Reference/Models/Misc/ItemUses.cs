using System.Collections.Generic;

namespace Mithril.Reference.Models.Misc;

/// <summary>
/// One entry from <c>itemuses.json</c>; keyed by item id (e.g. <c>"item_5010"</c>).
/// Lists every recipe that consumes this item as an ingredient.
/// </summary>
public sealed class ItemUses
{
    /// <summary>Numeric recipe ids; resolve to recipe internal names via recipes.json.</summary>
    public IReadOnlyList<long>? RecipesThatUseItem { get; set; }
}
