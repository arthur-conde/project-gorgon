namespace Mithril.Shared.Wpf.Sorting;

/// <summary>
/// Declarative description of one property a collection can be sorted by.
/// Authored once per consumer view-model; surfaced through <see cref="ISortableViewModel{T}"/>.
/// </summary>
/// <param name="Id">Stable identifier used for persistence and lookup. Must be unique within a single VM's <see cref="ISortableViewModel{T}.AvailableSortKeys"/>.</param>
/// <param name="DisplayName">User-visible label shown in sort tags.</param>
/// <param name="SortMemberPath">Property path passed to <see cref="System.ComponentModel.SortDescription"/>. Required because <see cref="System.Windows.Data.CollectionView.SortDescriptions"/> is path-based.</param>
/// <param name="DefaultDescending">Direction used when this key is first added to the active list.</param>
/// <param name="KeySelector">Optional in-memory key extractor for callers that want to sort outside a <see cref="System.Windows.Data.ICollectionView"/>.</param>
public sealed record SortKey<T>(
    string Id,
    string DisplayName,
    string SortMemberPath,
    bool DefaultDescending = false,
    Func<T, object?>? KeySelector = null);
