using System.Collections;

namespace Mithril.Shared.Wpf.Filtering;

/// <summary>
/// Non-generic facet so the popup can bind without knowing <c>T</c>.
/// </summary>
public interface IFilterableViewModel
{
    IEnumerable AvailableFiltersUntyped { get; }
}

/// <summary>
/// View-models expose this to participate in the shared filter popup.
/// Implementers only need to declare <see cref="AvailableFilters"/>; the
/// non-generic facet is wired via default interface member.
/// </summary>
public interface IFilterableViewModel<T> : IFilterableViewModel
{
    IReadOnlyList<FilterPredicate<T>> AvailableFilters { get; }

    IEnumerable IFilterableViewModel.AvailableFiltersUntyped => AvailableFilters;
}
