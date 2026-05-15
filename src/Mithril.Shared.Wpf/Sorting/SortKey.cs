namespace Mithril.Shared.Wpf.Sorting;

/// <summary>
/// Declarative description of one property a collection can be sorted by.
/// </summary>
/// <param name="Id">Stable identifier; the column name used in <c>ORDER BY</c>. Must be a real public property on <typeparamref name="T"/> (sort resolves via WPF's reflection-based <c>SortDescription</c> path).</param>
/// <param name="DisplayName">User-visible label for the chip.</param>
/// <param name="DefaultDescending">Initial direction when first toggled active.</param>
public sealed record SortKey<T>(
    string Id,
    string DisplayName,
    bool DefaultDescending = false);
