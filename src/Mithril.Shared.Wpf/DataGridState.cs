namespace Mithril.Shared.Wpf;

/// <summary>
/// Persisted state for a DataGrid: column order, widths, sort, and per-column filter text.
/// </summary>
public sealed class DataGridState
{
    public List<ColumnState> Columns { get; set; } = [];
}

/// <summary>
/// Persisted state for a single DataGrid column, keyed by <see cref="Key"/>
/// which matches the column's <c>SortMemberPath</c>.
/// </summary>
public sealed class ColumnState
{
    /// <summary>Matches <c>DataGridColumn.SortMemberPath</c>.</summary>
    public string Key { get; set; } = "";

    public int DisplayIndex { get; set; }

    /// <summary>Pixel width, or <see cref="double.NaN"/> for default/star sizing.</summary>
    public double Width { get; set; } = double.NaN;

    /// <summary>"Ascending", "Descending", or <c>null</c> for unsorted.</summary>
    public string? SortDirection { get; set; }

    /// <summary>Case-insensitive contains filter. Empty string means no filter.</summary>
    public string FilterText { get; set; } = "";
}
