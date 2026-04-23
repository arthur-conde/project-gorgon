using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Globalization;
using System.Linq;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using Gorgon.Shared.Wpf.Query;

namespace Gorgon.Shared.Wpf;

public enum GridMode
{
    ReadOnly,
    Editable,
}

/// <summary>
/// Themed <see cref="DataGrid"/> that accepts a SQL-like filter expression via
/// <see cref="QueryText"/> (driven by a companion <see cref="GorgonQueryBox"/>),
/// with a bare-text fallback that substring-matches string columns. Composes its
/// predicate on top of any filter the VM has already set on the bound
/// <see cref="ICollectionView"/>.
/// </summary>
public class GorgonDataGrid : DataGrid
{
    public static readonly DependencyProperty ModeProperty = DependencyProperty.Register(
        nameof(Mode), typeof(GridMode), typeof(GorgonDataGrid),
        new FrameworkPropertyMetadata(GridMode.ReadOnly, OnModeChanged));

    public static readonly DependencyProperty QueryTextProperty = DependencyProperty.Register(
        nameof(QueryText), typeof(string), typeof(GorgonDataGrid),
        new FrameworkPropertyMetadata(string.Empty, FrameworkPropertyMetadataOptions.BindsTwoWayByDefault, OnFilterInputChanged));

    public static readonly DependencyProperty QueryErrorProperty = DependencyProperty.Register(
        nameof(QueryError), typeof(string), typeof(GorgonDataGrid),
        new FrameworkPropertyMetadata(null));

    public static readonly DependencyProperty FilterCaseSensitiveProperty = DependencyProperty.Register(
        nameof(FilterCaseSensitive), typeof(bool), typeof(GorgonDataGrid),
        new FrameworkPropertyMetadata(false, OnFilterInputChanged));

    public static readonly DependencyProperty QueryNameProperty = DependencyProperty.RegisterAttached(
        "QueryName", typeof(string), typeof(GorgonDataGrid),
        new FrameworkPropertyMetadata(null));

    public GridMode Mode
    {
        get => (GridMode)GetValue(ModeProperty);
        set => SetValue(ModeProperty, value);
    }

    public string QueryText
    {
        get => (string)GetValue(QueryTextProperty);
        set => SetValue(QueryTextProperty, value);
    }

    public string? QueryError
    {
        get => (string?)GetValue(QueryErrorProperty);
        set => SetValue(QueryErrorProperty, value);
    }

    public bool FilterCaseSensitive
    {
        get => (bool)GetValue(FilterCaseSensitiveProperty);
        set => SetValue(FilterCaseSensitiveProperty, value);
    }

    public static string? GetQueryName(DependencyObject obj) => (string?)obj.GetValue(QueryNameProperty);
    public static void SetQueryName(DependencyObject obj, string? value) => obj.SetValue(QueryNameProperty, value);

    private const int DistinctValueSampleLimit = 50;

    private readonly DispatcherTimer _debounceTimer;
    private Predicate<object>? _vmFilter;
    private ICollectionView? _attachedView;
    private Dictionary<string, ColumnBinding> _columns = new(StringComparer.OrdinalIgnoreCase);
    private Dictionary<string, List<string>> _distinctValues = new(StringComparer.OrdinalIgnoreCase);
    private Type? _itemType;

    /// <summary>
    /// Snapshot of the grid's filterable columns — query name, CLR type, nullability.
    /// Consumers (like <c>GorgonQueryBox</c>) pull this to drive completion.
    /// </summary>
    public IReadOnlyList<Query.ColumnSchema> GetColumnSchema()
    {
        var list = new List<Query.ColumnSchema>();
        foreach (var binding in _columns.Values)
        {
            var underlying = Nullable.GetUnderlyingType(binding.ValueType);
            var isNullable = underlying is not null || !binding.ValueType.IsValueType;
            list.Add(new Query.ColumnSchema(binding.Name, binding.ValueType, isNullable));
        }
        return list;
    }

    /// <summary>
    /// Up to 50 distinct values currently present in <paramref name="columnName"/>,
    /// formatted via the row items' <c>ToString</c>. Intended for string-column
    /// value suggestions in completion.
    /// </summary>
    public IReadOnlyList<string> GetDistinctValues(string columnName)
    {
        if (_distinctValues.TryGetValue(columnName, out var list))
        {
            return list;
        }
        return Array.Empty<string>();
    }

    public GorgonDataGrid()
    {
        _debounceTimer = new DispatcherTimer
        {
            Interval = TimeSpan.FromMilliseconds(250),
        };
        _debounceTimer.Tick += (_, _) =>
        {
            _debounceTimer.Stop();
            RebuildFilter();
        };

        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
    }

    private static void OnModeChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is GorgonDataGrid g)
        {
            g.IsReadOnly = g.Mode == GridMode.ReadOnly;
        }
    }

    private static void OnFilterInputChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is GorgonDataGrid g)
        {
            g.ScheduleFilterRebuild();
        }
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        AttachToSource();
    }

    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        DetachFromSource();
    }

    protected override void OnItemsSourceChanged(System.Collections.IEnumerable oldValue, System.Collections.IEnumerable newValue)
    {
        base.OnItemsSourceChanged(oldValue, newValue);
        DetachFromSource();
        if (IsLoaded)
        {
            AttachToSource();
        }
    }

    /// <summary>
    /// Fires whenever <see cref="ItemsSource"/> or the underlying items change in a
    /// way that affects the completion schema or distinct-value samples.
    /// </summary>
    public event EventHandler? SchemaChanged;

    private void AttachToSource()
    {
        if (ItemsSource is null)
        {
            return;
        }

        _itemType = InferItemType(ItemsSource);
        if (_itemType is not null)
        {
            _columns = BuildColumnBindings(_itemType);
        }
        else
        {
            _columns = new(StringComparer.OrdinalIgnoreCase);
        }

        _attachedView = CollectionViewSource.GetDefaultView(ItemsSource);
        _vmFilter = _attachedView?.Filter;
        RebuildDistinctValues();
        RebuildFilter();
        SchemaChanged?.Invoke(this, EventArgs.Empty);
    }

    private void RebuildDistinctValues()
    {
        _distinctValues = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
        if (ItemsSource is null)
        {
            return;
        }
        var stringCols = _columns.Values
            .Where(c => (Nullable.GetUnderlyingType(c.ValueType) ?? c.ValueType) == typeof(string))
            .ToList();
        if (stringCols.Count == 0)
        {
            return;
        }
        var perColumn = stringCols.ToDictionary(
            c => c.Name,
            _ => new HashSet<string>(StringComparer.OrdinalIgnoreCase),
            StringComparer.OrdinalIgnoreCase);

        foreach (var item in ItemsSource)
        {
            if (item is null)
            {
                continue;
            }
            bool anyStillGrowing = false;
            foreach (var c in stringCols)
            {
                var set = perColumn[c.Name];
                if (set.Count >= DistinctValueSampleLimit)
                {
                    continue;
                }
                var v = c.GetValue(item) as string;
                if (!string.IsNullOrEmpty(v))
                {
                    set.Add(v);
                }
                anyStillGrowing = true;
            }
            if (!anyStillGrowing)
            {
                break;
            }
        }

        foreach (var (name, set) in perColumn)
        {
            _distinctValues[name] = set.OrderBy(s => s, StringComparer.OrdinalIgnoreCase).ToList();
        }
    }

    private void DetachFromSource()
    {
        if (_attachedView is not null)
        {
            _attachedView.Filter = _vmFilter;
            _attachedView = null;
        }
        _vmFilter = null;
    }

    private static Type? InferItemType(System.Collections.IEnumerable source)
    {
        var type = source.GetType();
        foreach (var iface in type.GetInterfaces())
        {
            if (iface.IsGenericType && iface.GetGenericTypeDefinition() == typeof(IEnumerable<>))
            {
                return iface.GetGenericArguments()[0];
            }
        }
        // Fallback: peek first item.
        foreach (var item in source)
        {
            return item?.GetType();
        }
        return null;
    }

    private Dictionary<string, ColumnBinding> BuildColumnBindings(Type itemType)
    {
        var map = new Dictionary<string, ColumnBinding>(StringComparer.OrdinalIgnoreCase);

        // Start with every public property on the item type — users can query any bound data.
        foreach (var prop in itemType.GetProperties(BindingFlags.Public | BindingFlags.Instance))
        {
            if (prop.GetIndexParameters().Length > 0)
            {
                continue;
            }
            var getter = BuildGetter(prop);
            map[prop.Name] = new ColumnBinding(prop.Name, prop.PropertyType, getter);
        }

        // Overlay explicit QueryName attached-property mappings from columns.
        foreach (var col in Columns)
        {
            var queryName = GetQueryName(col) ?? col.SortMemberPath;
            if (string.IsNullOrEmpty(queryName))
            {
                continue;
            }
            var prop = itemType.GetProperty(col.SortMemberPath ?? "", BindingFlags.Public | BindingFlags.Instance);
            if (prop is null)
            {
                continue;
            }
            map[queryName] = new ColumnBinding(queryName, prop.PropertyType, BuildGetter(prop));
        }

        return map;
    }

    private static Func<object, object?> BuildGetter(PropertyInfo prop)
    {
        return item =>
        {
            try
            {
                return prop.GetValue(item);
            }
            catch
            {
                return null;
            }
        };
    }

    private void ScheduleFilterRebuild()
    {
        _debounceTimer.Stop();
        _debounceTimer.Start();
    }

    private void RebuildFilter()
    {
        if (_attachedView is null)
        {
            return;
        }

        var queryPredicate = BuildInputPredicate(QueryText);
        var vm = _vmFilter;

        _attachedView.Filter = item =>
        {
            if (vm is not null && !vm(item))
            {
                return false;
            }
            if (queryPredicate is not null && !queryPredicate(item))
            {
                return false;
            }
            return true;
        };
    }

    /// <summary>
    /// Interprets <paramref name="text"/> as either a grammar expression or a
    /// bare-text substring search against string columns, based on
    /// <see cref="QueryParser.LooksLikeGrammar"/>. Grammar that fails to compile
    /// surfaces in <see cref="QueryError"/> and leaves the previous predicate active.
    /// </summary>
    private Predicate<object>? BuildInputPredicate(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            QueryError = null;
            return null;
        }

        var knownColumns = new HashSet<string>(_columns.Keys, StringComparer.OrdinalIgnoreCase);
        if (QueryParser.LooksLikeGrammar(text, knownColumns))
        {
            try
            {
                var compiled = QueryCompiler.Compile(text, _columns, FilterCaseSensitive);
                QueryError = null;
                return compiled is null ? null : item => compiled(item);
            }
            catch (QueryException qex)
            {
                QueryError = qex.Message;
                return _lastGoodInputPredicate;
            }
        }

        QueryError = null;
        var predicate = BuildBareTextPredicate(text);
        _lastGoodInputPredicate = predicate;
        return predicate;
    }

    private Predicate<object>? _lastGoodInputPredicate;

    /// <summary>
    /// Bare-text fallback: matches rows whose string columns contain <paramref name="text"/>
    /// as a case-insensitive substring. Numeric / date / duration columns are not
    /// considered — the tooltip on the query box communicates this.
    /// </summary>
    private Predicate<object>? BuildBareTextPredicate(string text)
    {
        var cmp = FilterCaseSensitive ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase;
        var needle = text.Trim();
        if (needle.Length == 0)
        {
            return null;
        }
        var stringBindings = _columns.Values
            .Where(c => (Nullable.GetUnderlyingType(c.ValueType) ?? c.ValueType) == typeof(string))
            .ToArray();
        if (stringBindings.Length == 0)
        {
            return _ => false;
        }
        return item =>
        {
            foreach (var col in stringBindings)
            {
                if (col.GetValue(item) is string s && s.Contains(needle, cmp))
                {
                    return true;
                }
            }
            return false;
        };
    }

}
