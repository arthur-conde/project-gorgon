using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Windows.Data;
using Mithril.Shared.Wpf.Filtering;

namespace Mithril.Shared.Wpf.Sorting;

/// <summary>
/// Wires an <see cref="ICollectionView"/>'s <see cref="ICollectionView.SortDescriptions"/> and
/// <see cref="ICollectionView.Filter"/> to the active sort/filter state owned by a view-model,
/// so consumers don't have to write the boilerplate themselves.
/// </summary>
public sealed class SortFilterController<T> : IDisposable
{
    private readonly ICollectionView _view;
    private readonly ObservableCollection<ActiveSortKey<T>> _activeSortKeys;
    private readonly IReadOnlyList<FilterPredicate<T>> _filters;
    private bool _disposed;

    public SortFilterController(
        ICollectionView view,
        ObservableCollection<ActiveSortKey<T>> activeSortKeys,
        IReadOnlyList<FilterPredicate<T>> filters)
    {
        _view = view ?? throw new ArgumentNullException(nameof(view));
        _activeSortKeys = activeSortKeys ?? throw new ArgumentNullException(nameof(activeSortKeys));
        _filters = filters ?? throw new ArgumentNullException(nameof(filters));

        _view.Filter = MatchesActiveFilters;

        _activeSortKeys.CollectionChanged += OnActiveSortKeysChanged;
        foreach (var key in _activeSortKeys)
            key.PropertyChanged += OnActiveSortKeyPropertyChanged;

        foreach (var filter in _filters)
            filter.PropertyChanged += OnFilterPropertyChanged;

        RebuildSortDescriptions();
    }

    private bool MatchesActiveFilters(object item)
    {
        if (item is not T typed) return true;
        foreach (var f in _filters)
        {
            if (!f.ShouldApply) continue;
            if (!f.Predicate(typed)) return false;
        }
        return true;
    }

    private void OnActiveSortKeysChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (e.OldItems is not null)
            foreach (ActiveSortKey<T> k in e.OldItems)
                k.PropertyChanged -= OnActiveSortKeyPropertyChanged;

        if (e.NewItems is not null)
            foreach (ActiveSortKey<T> k in e.NewItems)
                k.PropertyChanged += OnActiveSortKeyPropertyChanged;

        if (e.Action == NotifyCollectionChangedAction.Reset)
            foreach (var k in _activeSortKeys)
                k.PropertyChanged += OnActiveSortKeyPropertyChanged;

        RebuildSortDescriptions();
    }

    private void OnActiveSortKeyPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(ActiveSortKey<T>.Direction))
            RebuildSortDescriptions();
    }

    private void OnFilterPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(FilterPredicate<T>.IsActive)
            || e.PropertyName == nameof(FilterPredicate<T>.ShouldApply))
            _view.Refresh();
    }

    private void RebuildSortDescriptions()
    {
        using (_view.DeferRefresh())
        {
            _view.SortDescriptions.Clear();
            foreach (var key in _activeSortKeys)
                _view.SortDescriptions.Add(new SortDescription(key.Key.SortMemberPath, key.Direction));
        }
    }

    /// <summary>Re-evaluates filters without changing sort state. Call after closures' captured state changes (e.g. selecting a different skill).</summary>
    public void RefreshFilters() => _view.Refresh();

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        _activeSortKeys.CollectionChanged -= OnActiveSortKeysChanged;
        foreach (var key in _activeSortKeys)
            key.PropertyChanged -= OnActiveSortKeyPropertyChanged;
        foreach (var filter in _filters)
            filter.PropertyChanged -= OnFilterPropertyChanged;
    }
}
