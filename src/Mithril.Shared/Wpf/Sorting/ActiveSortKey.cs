using System.ComponentModel;
using CommunityToolkit.Mvvm.ComponentModel;

namespace Mithril.Shared.Wpf.Sorting;

/// <summary>
/// One entry in the ordered list of active sort keys. Wraps a <see cref="SortKey{T}"/>
/// with a mutable <see cref="Direction"/> and a glyph for the popup tag.
/// </summary>
public sealed partial class ActiveSortKey<T> : ObservableObject
{
    public ActiveSortKey(SortKey<T> key, ListSortDirection direction)
    {
        Key = key;
        _direction = direction;
    }

    public SortKey<T> Key { get; }

    public string Id => Key.Id;

    public string DisplayName => Key.DisplayName;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IndicatorGlyph))]
    private ListSortDirection _direction;

    public string IndicatorGlyph => Direction == ListSortDirection.Ascending ? "▲" : "▼";

    public void FlipDirection() =>
        Direction = Direction == ListSortDirection.Ascending
            ? ListSortDirection.Descending
            : ListSortDirection.Ascending;
}
