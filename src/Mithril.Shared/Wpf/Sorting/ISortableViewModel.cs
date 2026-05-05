using System.Collections;
using System.Collections.ObjectModel;
using System.ComponentModel;

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
/// Implementers only need to declare <see cref="AvailableSortKeys"/> and
/// <see cref="ActiveSortKeys"/>; the non-generic facets and mutation methods
/// are wired via default interface members.
/// </summary>
public interface ISortableViewModel<T> : ISortableViewModel
{
    IReadOnlyList<SortKey<T>> AvailableSortKeys { get; }
    ObservableCollection<ActiveSortKey<T>> ActiveSortKeys { get; }

    IEnumerable ISortableViewModel.AvailableSortKeysUntyped => AvailableSortKeys;
    IList ISortableViewModel.ActiveSortKeysUntyped => ActiveSortKeys;

    void ISortableViewModel.AddSortKeyById(string id)
    {
        var key = AvailableSortKeys.FirstOrDefault(k => k.Id == id);
        if (key is null) return;
        if (ActiveSortKeys.Any(a => a.Key.Id == id)) return;
        var dir = key.DefaultDescending ? ListSortDirection.Descending : ListSortDirection.Ascending;
        ActiveSortKeys.Add(new ActiveSortKey<T>(key, dir));
    }

    void ISortableViewModel.RemoveSortKeyAt(int index)
    {
        if ((uint)index < (uint)ActiveSortKeys.Count)
            ActiveSortKeys.RemoveAt(index);
    }

    void ISortableViewModel.MoveSortKey(int fromIndex, int toIndex)
    {
        if ((uint)fromIndex >= (uint)ActiveSortKeys.Count) return;
        if ((uint)toIndex   >= (uint)ActiveSortKeys.Count) return;
        if (fromIndex == toIndex) return;
        ActiveSortKeys.Move(fromIndex, toIndex);
    }
}
