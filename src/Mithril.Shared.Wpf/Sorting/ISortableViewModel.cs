using System.Collections;
using System.Collections.Generic;

namespace Mithril.Shared.Wpf.Sorting;

/// <summary>
/// Non-generic facet for the shared sort popup binding.
/// </summary>
public interface ISortableViewModel
{
    IEnumerable ChipsUntyped { get; }
    void ToggleChip(string id);
}

/// <summary>
/// View-models expose this to participate in the shared sort popup. Implementers
/// surface the projected <see cref="Chips"/> from their
/// <see cref="SortFilterController{T}"/> and forward chip clicks via
/// <see cref="ISortableViewModel.ToggleChip"/>.
/// </summary>
public interface ISortableViewModel<T> : ISortableViewModel
{
    IReadOnlyList<ChipState<T>> Chips { get; }

    IEnumerable ISortableViewModel.ChipsUntyped => Chips;
}
