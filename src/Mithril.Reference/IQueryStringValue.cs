namespace Mithril.Reference;

/// <summary>
/// Opt-in marker for collection-element types that should be matchable by the
/// Mithril query engine's <c>CONTAINS</c> operator. When a column is an
/// <c>IEnumerable&lt;T&gt;</c> whose element type implements this interface,
/// <c>Column CONTAINS 'X'</c> returns true when any element's
/// <see cref="QueryStringValue"/> equals <c>X</c>. The engine performs a
/// case-insensitive comparison by default (matching its standard string
/// semantics); pass <c>caseSensitive: true</c> at compile time to switch to
/// ordinal equality. Equality semantics (not substring) — mirroring the
/// existing behaviour for collections of <c>string</c>.
/// </summary>
/// <remarks>
/// Lives in <c>Mithril.Reference</c> (the leaf project) so that model types
/// declared here — e.g. <see cref="Models.Items.ItemKeyword"/> — can opt in
/// without forcing a dependency on <c>Mithril.Shared.Wpf</c>. The query
/// compiler picks the interface up transitively.
/// </remarks>
public interface IQueryStringValue
{
    /// <summary>
    /// The element's string projection used by the query engine for
    /// <c>CONTAINS</c> matching.
    /// </summary>
    string QueryStringValue { get; }
}
