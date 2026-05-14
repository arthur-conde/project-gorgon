using Mithril.Reference;

namespace Silmarillion.ViewModels;

/// <summary>
/// Lightweight wrapper that lets an effect's <c>Keywords</c> list participate in the
/// query engine's collection-<c>CONTAINS</c> path (see <see cref="IQueryStringValue"/>,
/// shipped in PR #261). Exposes the raw keyword <see cref="Tag"/> as the queryable string
/// so <c>Keywords CONTAINS "Buff"</c> works on the Effects tab the same way
/// <see cref="IngredientKeywordValue"/> handles recipe-side keyword filtering.
/// </summary>
public sealed record EffectKeywordValue(string Tag) : IQueryStringValue
{
    public string QueryStringValue => Tag;
}
