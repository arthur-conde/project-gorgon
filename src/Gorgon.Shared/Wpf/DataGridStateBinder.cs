using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;

namespace Gorgon.Shared.Wpf;

/// <summary>
/// Binds a <see cref="DataGrid"/> to a <see cref="DataGridState"/>, restoring saved
/// column order/widths/sort/filters on load and persisting changes via a callback.
/// Follows the same static-helper pattern as <c>WindowLayoutBinder</c>.
/// </summary>
public static class DataGridStateBinder
{
    /// <summary>
    /// Binds the grid's column layout, sort, and per-column filter state to
    /// <paramref name="state"/>. Call once after construction; the binder defers
    /// actual work until the <see cref="FrameworkElement.Loaded"/> event.
    /// </summary>
    /// <param name="grid">The DataGrid to bind.</param>
    /// <param name="state">Persisted state object (lives inside the module's settings).</param>
    /// <param name="applyFilter">Called when sort or filter changes — the VM should re-run its filter pipeline.</param>
    /// <param name="onChanged">Called on any state change — typically <c>SettingsAutoSaver.Touch</c>.</param>
    public static void Bind(DataGrid grid, DataGridState state, Action applyFilter, Action? onChanged = null)
    {
        if (grid.IsLoaded)
            Attach(grid, state, applyFilter, onChanged);
        else
            grid.Loaded += (_, _) => Attach(grid, state, applyFilter, onChanged);
    }

    private static void Attach(DataGrid grid, DataGridState state, Action applyFilter, Action? onChanged)
    {
        EnsureColumns(grid, state);
        RestoreColumnLayout(grid, state);
        RestoreSort(grid, state);

        // Find filter TextBoxes inside column headers and restore/hook them.
        // We need to defer slightly because the visual tree isn't fully built at Loaded.
        grid.Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Render, () =>
        {
            HookFilterBoxes(grid, state, applyFilter, onChanged);
        });

        // Hook sorting
        grid.Sorting += (_, e) =>
        {
            e.Handled = true;
            HandleSort(grid, state, e.Column, applyFilter, onChanged);
        };

        // Hook column reorder
        grid.ColumnReordered += (_, _) =>
        {
            SyncDisplayIndexes(grid, state);
            onChanged?.Invoke();
        };

        // Hook column width changes via layout updates
        grid.LayoutUpdated += CreateWidthTracker(grid, state, onChanged);
    }

    /// <summary>
    /// Ensures <paramref name="state"/> has a <see cref="ColumnState"/> entry for every
    /// column in the grid (keyed by <c>SortMemberPath</c>), and removes stale entries.
    /// </summary>
    private static void EnsureColumns(DataGrid grid, DataGridState state)
    {
        var existing = state.Columns.ToDictionary(c => c.Key, StringComparer.OrdinalIgnoreCase);
        var ordered = new List<ColumnState>();

        foreach (var col in grid.Columns)
        {
            var key = GetColumnKey(col);
            if (string.IsNullOrEmpty(key)) continue;

            if (existing.TryGetValue(key, out var saved))
            {
                ordered.Add(saved);
            }
            else
            {
                ordered.Add(new ColumnState
                {
                    Key = key,
                    DisplayIndex = col.DisplayIndex,
                    Width = double.NaN,
                });
            }
        }

        state.Columns = ordered;
    }

    private static void RestoreColumnLayout(DataGrid grid, DataGridState state)
    {
        var lookup = state.Columns.ToDictionary(c => c.Key, StringComparer.OrdinalIgnoreCase);

        foreach (var col in grid.Columns)
        {
            var key = GetColumnKey(col);
            if (string.IsNullOrEmpty(key) || !lookup.TryGetValue(key, out var cs)) continue;

            if (!double.IsNaN(cs.Width) && cs.Width > 0)
                col.Width = new DataGridLength(cs.Width);
        }

        // Restore display indexes — must be done carefully to avoid index conflicts.
        // Sort columns by their saved display index and assign sequentially.
        var columnsWithSavedIndex = grid.Columns
            .Select(col => (col, key: GetColumnKey(col)))
            .Where(t => !string.IsNullOrEmpty(t.key) && lookup.ContainsKey(t.key!))
            .OrderBy(t => lookup[t.key!].DisplayIndex)
            .ToList();

        for (int i = 0; i < columnsWithSavedIndex.Count; i++)
        {
            var target = Math.Min(i, grid.Columns.Count - 1);
            columnsWithSavedIndex[i].col.DisplayIndex = target;
        }
    }

    private static void RestoreSort(DataGrid grid, DataGridState state)
    {
        var sortCol = state.Columns.FirstOrDefault(c => c.SortDirection is not null);
        if (sortCol is null) return;

        var col = grid.Columns.FirstOrDefault(c =>
            string.Equals(GetColumnKey(c), sortCol.Key, StringComparison.OrdinalIgnoreCase));
        if (col is null) return;

        col.SortDirection = sortCol.SortDirection switch
        {
            "Ascending" => ListSortDirection.Ascending,
            "Descending" => ListSortDirection.Descending,
            _ => null,
        };
    }

    private static void HandleSort(
        DataGrid grid, DataGridState state, DataGridColumn column,
        Action applyFilter, Action? onChanged)
    {
        var key = GetColumnKey(column);
        if (string.IsNullOrEmpty(key)) return;

        // Cycle: None → Ascending → Descending → None
        var current = column.SortDirection;
        ListSortDirection? next = current switch
        {
            null => ListSortDirection.Ascending,
            ListSortDirection.Ascending => ListSortDirection.Descending,
            _ => null,
        };

        // Clear all columns' sort
        foreach (var col in grid.Columns)
            col.SortDirection = null;
        foreach (var cs in state.Columns)
            cs.SortDirection = null;

        // Set the new sort
        column.SortDirection = next;
        var colState = state.Columns.FirstOrDefault(c =>
            string.Equals(c.Key, key, StringComparison.OrdinalIgnoreCase));
        if (colState is not null)
        {
            colState.SortDirection = next switch
            {
                ListSortDirection.Ascending => "Ascending",
                ListSortDirection.Descending => "Descending",
                _ => null,
            };
        }

        applyFilter();
        onChanged?.Invoke();
    }

    private static void HookFilterBoxes(
        DataGrid grid, DataGridState state, Action applyFilter, Action? onChanged)
    {
        var lookup = state.Columns.ToDictionary(c => c.Key, StringComparer.OrdinalIgnoreCase);

        foreach (var col in grid.Columns)
        {
            var key = GetColumnKey(col);
            if (string.IsNullOrEmpty(key)) continue;

            var header = FindColumnHeader(grid, col);
            if (header is null) continue;

            var filterBox = FindChild<TextBox>(header, "PART_FilterBox");
            if (filterBox is null) continue;

            // Restore saved filter text
            if (lookup.TryGetValue(key, out var cs) && !string.IsNullOrEmpty(cs.FilterText))
            {
                filterBox.Text = cs.FilterText;
            }

            // Hook text changes
            var capturedKey = key;
            filterBox.TextChanged += (_, _) =>
            {
                if (lookup.TryGetValue(capturedKey, out var colState))
                {
                    colState.FilterText = filterBox.Text;
                }
                applyFilter();
                onChanged?.Invoke();
            };

            // Prevent header click-to-sort when clicking inside the filter box
            filterBox.PreviewMouseDown += (_, e) => e.Handled = true;
        }

        // If we restored any filter text, apply filters now
        if (state.Columns.Any(c => !string.IsNullOrEmpty(c.FilterText)))
            applyFilter();
    }

    private static void SyncDisplayIndexes(DataGrid grid, DataGridState state)
    {
        var lookup = state.Columns.ToDictionary(c => c.Key, StringComparer.OrdinalIgnoreCase);
        foreach (var col in grid.Columns)
        {
            var key = GetColumnKey(col);
            if (!string.IsNullOrEmpty(key) && lookup.TryGetValue(key, out var cs))
                cs.DisplayIndex = col.DisplayIndex;
        }
    }

    private static EventHandler CreateWidthTracker(DataGrid grid, DataGridState state, Action? onChanged)
    {
        var lookup = state.Columns.ToDictionary(c => c.Key, StringComparer.OrdinalIgnoreCase);
        var lastWidths = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);

        return (_, _) =>
        {
            bool changed = false;
            foreach (var col in grid.Columns)
            {
                var key = GetColumnKey(col);
                if (string.IsNullOrEmpty(key) || !lookup.TryGetValue(key, out var cs)) continue;

                var actual = col.ActualWidth;
                if (actual <= 0) continue;

                lastWidths.TryGetValue(key, out var prev);
                if (Math.Abs(actual - prev) > 0.5)
                {
                    lastWidths[key] = actual;
                    cs.Width = actual;
                    changed = true;
                }
            }
            if (changed)
                onChanged?.Invoke();
        };
    }

    private static string GetColumnKey(DataGridColumn col)
    {
        if (!string.IsNullOrEmpty(col.SortMemberPath))
            return col.SortMemberPath;
        return col.Header?.ToString() ?? "";
    }

    // ── Visual tree helpers ──────────────────────────────────────────────

    private static DataGridColumnHeader? FindColumnHeader(DataGrid grid, DataGridColumn column)
    {
        // Walk the visual tree to find the header presenter, then the specific header
        var headerPresenter = FindChild<DataGridColumnHeadersPresenter>(grid);
        if (headerPresenter is null) return null;

        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(headerPresenter); i++)
        {
            if (VisualTreeHelper.GetChild(headerPresenter, i) is DataGridColumnHeader header
                && header.Column == column)
            {
                return header;
            }
        }

        // Try deeper search
        return FindChildByColumn(headerPresenter, column);
    }

    private static DataGridColumnHeader? FindChildByColumn(DependencyObject parent, DataGridColumn column)
    {
        int count = VisualTreeHelper.GetChildrenCount(parent);
        for (int i = 0; i < count; i++)
        {
            var child = VisualTreeHelper.GetChild(parent, i);
            if (child is DataGridColumnHeader header && header.Column == column)
                return header;

            var result = FindChildByColumn(child, column);
            if (result is not null)
                return result;
        }
        return null;
    }

    private static T? FindChild<T>(DependencyObject parent, string? name = null) where T : FrameworkElement
    {
        int count = VisualTreeHelper.GetChildrenCount(parent);
        for (int i = 0; i < count; i++)
        {
            var child = VisualTreeHelper.GetChild(parent, i);
            if (child is T match && (name is null || match.Name == name))
                return match;

            var result = FindChild<T>(child, name);
            if (result is not null)
                return result;
        }
        return null;
    }
}
