using Mithril.Reference;

namespace Silmarillion.ViewModels;

/// <summary>
/// Lightweight wrapper that lets a recipe's flattened ingredient-item set
/// participate in the query engine's collection-<c>CONTAINS</c> path. Exposes the
/// item's <c>InternalName</c> as the queryable string. Mirror of
/// <see cref="IngredientKeywordValue"/> for the item-pivot direction — powers
/// the item-detail "Used in" overflow pill deep-link
/// (<c>Ingredients CONTAINS "&lt;itemInternalName&gt;"</c>).
/// </summary>
public sealed record IngredientItemValue(string InternalName) : IQueryStringValue
{
    public string QueryStringValue => InternalName;
}
