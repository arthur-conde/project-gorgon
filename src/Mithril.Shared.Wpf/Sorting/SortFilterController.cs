using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Windows.Data;
using Mithril.Shared.Wpf.Filtering;
using Mithril.Shared.Wpf.Query;

namespace Mithril.Shared.Wpf.Sorting;

/// <summary>
/// Wires an <see cref="ICollectionView"/>'s filter + sort to the canonical
/// query text owned by a <see cref="MithrilQueryBox"/> (or another
/// <see cref="ParsedQuery"/> source). The view-model passes its declarative
/// list of available <see cref="SortKey{T}"/> entries and a list of
/// <see cref="FilterPredicate{T}"/> entries; the controller publishes a
/// <see cref="Chips"/> projection chips can bind to and edits the query text
/// when chips are clicked.
/// </summary>
public sealed class SortFilterController<T> : IDisposable, INotifyPropertyChanged
{
    private readonly ICollectionView _view;
    private readonly IReadOnlyList<SortKey<T>> _availableKeys;
    private readonly IReadOnlyList<FilterPredicate<T>> _filters;
    private readonly IReadOnlyDictionary<string, ColumnBinding> _columns;
    private readonly Action<IReadOnlyList<OrderSpec>> _rewriteOrder;
    private IReadOnlyList<OrderSpec> _currentOrder = Array.Empty<OrderSpec>();
    private bool _disposed;

    public SortFilterController(
        ICollectionView view,
        IReadOnlyList<SortKey<T>> availableKeys,
        IReadOnlyList<FilterPredicate<T>> filters,
        Action<IReadOnlyList<OrderSpec>> rewriteOrder)
    {
        _view = view ?? throw new ArgumentNullException(nameof(view));
        _availableKeys = availableKeys ?? throw new ArgumentNullException(nameof(availableKeys));
        _filters = filters ?? throw new ArgumentNullException(nameof(filters));
        _rewriteOrder = rewriteOrder ?? throw new ArgumentNullException(nameof(rewriteOrder));

        _columns = BuildSchemaFromKeys(availableKeys);
        _view.Filter = MatchesActiveFilters;
        foreach (var f in _filters)
            f.PropertyChanged += OnFilterPropertyChanged;

        RecomputeChips();
    }

    public IReadOnlyList<ChipState<T>> Chips { get; private set; } = Array.Empty<ChipState<T>>();

    public event PropertyChangedEventHandler? PropertyChanged;

    /// <summary>Call when the bound query-box's <c>ParsedQuery</c> changes.</summary>
    public void OnParsedOrderChanged(IReadOnlyList<OrderSpec> newOrder)
    {
        _currentOrder = newOrder ?? Array.Empty<OrderSpec>();

        using (_view.DeferRefresh())
        {
            _view.SortDescriptions.Clear();
            try
            {
                foreach (var sd in QueryCompiler.CompileOrder(_currentOrder, _columns))
                {
                    _view.SortDescriptions.Add(sd);
                }
            }
            catch (QueryException)
            {
                // Bad column name: leave descriptors empty; the predicate-side error
                // surface (query-box error chrome) shows the diagnostic.
            }
        }
        RecomputeChips();
    }

    /// <summary>
    /// Toggle a chip: if it's not in the current order, append it (default
    /// direction); if it is at its default direction, flip to the opposite;
    /// if it's at the flipped direction, remove it.
    /// </summary>
    public void ToggleChip(string keyId)
    {
        var key = _availableKeys.FirstOrDefault(k => string.Equals(k.Id, keyId, StringComparison.OrdinalIgnoreCase));
        if (key is null) return;

        int existingIndex = -1;
        for (int i = 0; i < _currentOrder.Count; i++)
        {
            if (string.Equals(_currentOrder[i].Column, keyId, StringComparison.OrdinalIgnoreCase))
            {
                existingIndex = i;
                break;
            }
        }

        IReadOnlyList<OrderSpec> next;
        if (existingIndex < 0)
        {
            var dir = key.DefaultDescending ? OrderDirection.Descending : OrderDirection.Ascending;
            next = _currentOrder.Concat(new[] { new OrderSpec(key.Id, dir) }).ToArray();
        }
        else
        {
            var defaultDir = key.DefaultDescending ? OrderDirection.Descending : OrderDirection.Ascending;
            var existing = _currentOrder[existingIndex];
            if (existing.Direction == defaultDir)
            {
                // Flip
                var list = _currentOrder.ToArray();
                list[existingIndex] = new OrderSpec(key.Id,
                    defaultDir == OrderDirection.Ascending ? OrderDirection.Descending : OrderDirection.Ascending);
                next = list;
            }
            else
            {
                // Remove
                next = _currentOrder.Where((_, i) => i != existingIndex).ToArray();
            }
        }
        _rewriteOrder(next);
    }

    /// <summary>Re-evaluate the view filter without changing sort.</summary>
    public void RefreshFilters() => _view.Refresh();

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        foreach (var f in _filters)
            f.PropertyChanged -= OnFilterPropertyChanged;
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

    private void OnFilterPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(FilterPredicate<T>.IsActive)
            || e.PropertyName == nameof(FilterPredicate<T>.ShouldApply))
            _view.Refresh();
    }

    private void RecomputeChips()
    {
        Chips = ChipState.Project(_availableKeys, _currentOrder);
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Chips)));
    }

    private static IReadOnlyDictionary<string, ColumnBinding> BuildSchemaFromKeys(
        IReadOnlyList<SortKey<T>> keys)
    {
        var map = ColumnBindingHelper.BuildFromProperties(typeof(T));
        foreach (var k in keys)
        {
            if (k.KeySelector is null) continue;
            var captured = k.KeySelector;
            map[k.Id] = new ColumnBinding(k.Id, typeof(object), item =>
            {
                if (item is T typed)
                {
                    try { return captured(typed); }
                    catch { return null; }
                }
                return null;
            });
        }
        return map;
    }
}
