using System.Collections;
using System.Collections.ObjectModel;

namespace Mithril.Shared.Wpf.Sorting;

/// <summary>
/// Non-generic facet so the popup can bind without knowing <c>T</c>.
/// </summary>
public interface ISortableViewModel
{
    IEnumerable AvailableSortKeysUntyped { get; }
    IList ActiveSortKeysUntyped { get; }

    void AddSortKeyById(string id);
    void RemoveSortKeyAt(int index);
    void MoveSortKey(int fromIndex, int toIndex);
}

/// <summary>
/// View-models expose this to participate in the shared sort popup.
/// </summary>
public interface ISortableViewModel<T> : ISortableViewModel
{
    IReadOnlyList<SortKey<T>> AvailableSortKeys { get; }
    ObservableCollection<ActiveSortKey<T>> ActiveSortKeys { get; }
}
