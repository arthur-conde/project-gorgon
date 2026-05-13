using Mithril.Reference;

namespace Silmarillion.ViewModels;

/// <summary>
/// Lightweight wrapper that lets a recipe's flattened ingredient-keyword set
/// participate in the query engine's collection-<c>CONTAINS</c> path (see
/// <see cref="IQueryStringValue"/>, shipped in PR #261). Exposes the raw
/// keyword <see cref="Tag"/> as the queryable string.
/// </summary>
public sealed record IngredientKeywordValue(string Tag) : IQueryStringValue
{
    public string QueryStringValue => Tag;
}
