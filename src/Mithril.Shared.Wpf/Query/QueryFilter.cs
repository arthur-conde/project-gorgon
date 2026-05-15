using System;
using System.Collections;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Threading;

namespace Mithril.Shared.Wpf.Query;

/// <summary>
/// Attached behaviour that lets any <see cref="ItemsControl"/> filter its
/// items by a SQL-like <c>QueryText</c> string, using the same grammar and
/// semantics as <c>MithrilDataGrid</c>/<c>MithrilQueryBox</c>. The schema is
/// reflected from the item type's public properties. The behaviour composes
/// onto any filter the VM has already set on the bound
/// <see cref="ICollectionView"/>, so existing filtering is preserved.
/// </summary>
/// <remarks>
/// <para>
/// Grammar input (detected via <see cref="QueryParser.LooksLikeGrammar"/>)
/// runs through <see cref="QueryCompiler"/>. Bare text falls back to a
/// case-insensitive substring search across the item type's string
/// properties — same UX contract as <c>MithrilDataGrid</c>.
/// </para>
/// <para>
/// Distinct-value sampling for completion is not part of this behaviour. If
/// you need completion against a non-DataGrid control, drive
/// <c>MithrilQueryBox.Schema</c> directly or pair this with
/// <see cref="QueryableSource{T}"/>.
/// </para>
/// </remarks>
public static class QueryFilter
{
    public static readonly DependencyProperty QueryTextProperty = DependencyProperty.RegisterAttached(
        "QueryText", typeof(string), typeof(QueryFilter),
        new FrameworkPropertyMetadata(string.Empty, FrameworkPropertyMetadataOptions.BindsTwoWayByDefault, OnQueryInputChanged));

    public static readonly DependencyProperty CaseSensitiveProperty = DependencyProperty.RegisterAttached(
        "CaseSensitive", typeof(bool), typeof(QueryFilter),
        new FrameworkPropertyMetadata(false, OnQueryInputChanged));

    public static readonly DependencyProperty QueryErrorProperty = DependencyProperty.RegisterAttached(
        "QueryError", typeof(string), typeof(QueryFilter),
        new FrameworkPropertyMetadata(null));

    private static readonly DependencyProperty StateProperty = DependencyProperty.RegisterAttached(
        "State", typeof(FilterState), typeof(QueryFilter),
        new PropertyMetadata(null));

    public static string GetQueryText(DependencyObject obj) => (string)obj.GetValue(QueryTextProperty);
    public static void SetQueryText(DependencyObject obj, string value) => obj.SetValue(QueryTextProperty, value);

    public static bool GetCaseSensitive(DependencyObject obj) => (bool)obj.GetValue(CaseSensitiveProperty);
    public static void SetCaseSensitive(DependencyObject obj, bool value) => obj.SetValue(CaseSensitiveProperty, value);

    public static string? GetQueryError(DependencyObject obj) => (string?)obj.GetValue(QueryErrorProperty);
    public static void SetQueryError(DependencyObject obj, string? value) => obj.SetValue(QueryErrorProperty, value);

    private static void OnQueryInputChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is ItemsControl ctrl)
        {
            EnsureState(ctrl).ScheduleRebuild();
        }
    }

    private static FilterState EnsureState(ItemsControl control)
    {
        var state = (FilterState?)control.GetValue(StateProperty);
        if (state is null)
        {
            state = new FilterState(control);
            control.SetValue(StateProperty, state);
        }
        return state;
    }

    /// <summary>
    /// For tests: synchronously flush any pending debounced rebuild. Returns
    /// <c>true</c> if a rebuild ran. No-op in production paths.
    /// </summary>
    internal static bool FlushPendingRebuildForTests(DependencyObject obj)
    {
        if (obj.GetValue(StateProperty) is FilterState state)
        {
            return state.FlushForTests();
        }
        return false;
    }

    /// <summary>
    /// For tests: force the attach path that normally only runs on
    /// <see cref="FrameworkElement.Loaded"/>. The control doesn't need to be
    /// in a visual tree.
    /// </summary>
    internal static void ForceAttachForTests(ItemsControl control)
    {
        EnsureState(control).AttachForTests();
    }

    /// <summary>
    /// For tests: force the detach path that normally only runs on
    /// <see cref="FrameworkElement.Unloaded"/>.
    /// </summary>
    internal static void ForceDetachForTests(ItemsControl control)
    {
        if (control.GetValue(StateProperty) is FilterState state)
        {
            state.DetachForTests();
        }
    }

    private sealed class FilterState
    {
        private const int DebounceMs = 250;
        private readonly ItemsControl _control;
        private readonly DispatcherTimer _debounce;
        private ICollectionView? _attachedView;
        private Predicate<object>? _vmFilter;
        private SortDescription[] _vmSortDescriptions = Array.Empty<SortDescription>();
        private Dictionary<string, ColumnBinding> _columns = new(StringComparer.OrdinalIgnoreCase);
        private Predicate<object>? _lastGoodInputPredicate;
        private bool _attached;

        public FilterState(ItemsControl control)
        {
            _control = control;
            _debounce = new DispatcherTimer
            {
                Interval = TimeSpan.FromMilliseconds(DebounceMs),
            };
            _debounce.Tick += (_, _) =>
            {
                _debounce.Stop();
                RebuildFilter();
            };

            control.Loaded += OnLoaded;
            control.Unloaded += OnUnloaded;
            // DependencyPropertyDescriptor keeps a strong ref to `control` for
            // the descriptor's lifetime; acceptable here because the state
            // lives as long as the control. (This is the same trade-off WPF
            // attached behaviours generally accept.)
            DependencyPropertyDescriptor
                .FromProperty(ItemsControl.ItemsSourceProperty, typeof(ItemsControl))
                .AddValueChanged(control, OnItemsSourceChanged);

            if (control.IsLoaded)
            {
                Attach();
            }
        }

        public void ScheduleRebuild()
        {
            if (!_attached) return;
            _debounce.Stop();
            _debounce.Start();
        }

        public bool FlushForTests()
        {
            if (!_debounce.IsEnabled) return false;
            _debounce.Stop();
            RebuildFilter();
            return true;
        }

        public void AttachForTests() => Attach();

        public void DetachForTests() => Detach();

        private void OnLoaded(object? sender, RoutedEventArgs e) => Attach();
        private void OnUnloaded(object? sender, RoutedEventArgs e) => Detach();
        private void OnItemsSourceChanged(object? sender, EventArgs e)
        {
            Detach();
            if (_control.IsLoaded) Attach();
        }

        private void Attach()
        {
            // Defensive: WPF can re-fire Loaded on a control whose visual tree
            // gets re-templated. Detach first so we re-capture the *VM's*
            // filter rather than our previous composite filter (otherwise we'd
            // wrap composites around composites and preserve stale predicates).
            if (_attached) Detach();
            if (_control.ItemsSource is null) return;

            var itemType = InferItemType(_control.ItemsSource);
            _columns = itemType is null
                ? new Dictionary<string, ColumnBinding>(StringComparer.OrdinalIgnoreCase)
                : ColumnBindingHelper.BuildFromProperties(itemType);

            _attachedView = CollectionViewSource.GetDefaultView(_control.ItemsSource);
            _vmFilter = _attachedView?.Filter;
            _vmSortDescriptions = _attachedView is null
                ? Array.Empty<SortDescription>()
                : _attachedView.SortDescriptions.ToArray();
            _attached = true;
            RebuildFilter();
        }

        private void Detach()
        {
            if (_attachedView is not null)
            {
                _attachedView.Filter = _vmFilter;
                _attachedView.SortDescriptions.Clear();
                foreach (var sd in _vmSortDescriptions)
                {
                    _attachedView.SortDescriptions.Add(sd);
                }
                _attachedView = null;
            }
            _vmFilter = null;
            _vmSortDescriptions = Array.Empty<SortDescription>();
            _attached = false;
        }

        private void RebuildFilter()
        {
            if (_attachedView is null) return;
            var input = GetQueryText(_control);
            var caseSensitive = GetCaseSensitive(_control);

            var (queryPredicate, parsedOrder) = BuildInputPredicateAndOrder(input, caseSensitive);
            var vm = _vmFilter;
            var vmSort = _vmSortDescriptions;

            using (_attachedView.DeferRefresh())
            {
                _attachedView.Filter = item =>
                {
                    if (vm is not null && !vm(item)) return false;
                    if (queryPredicate is not null && !queryPredicate(item)) return false;
                    return true;
                };

                _attachedView.SortDescriptions.Clear();
                if (parsedOrder.Count > 0)
                {
                    try
                    {
                        foreach (var sd in QueryCompiler.CompileOrder(parsedOrder, _columns, caseSensitive))
                        {
                            _attachedView.SortDescriptions.Add(sd);
                        }
                    }
                    catch (QueryException ex)
                    {
                        SetQueryError(_control, ex.Message);
                        foreach (var sd in vmSort)
                        {
                            _attachedView.SortDescriptions.Add(sd);
                        }
                    }
                }
                else
                {
                    foreach (var sd in vmSort)
                    {
                        _attachedView.SortDescriptions.Add(sd);
                    }
                }
            }
        }

        private (Predicate<object>? Predicate, IReadOnlyList<OrderSpec> Order) BuildInputPredicateAndOrder(
            string? text, bool caseSensitive)
        {
            if (string.IsNullOrWhiteSpace(text))
            {
                SetQueryError(_control, null);
                return (null, Array.Empty<OrderSpec>());
            }
            var knownColumns = new HashSet<string>(_columns.Keys, StringComparer.OrdinalIgnoreCase);
            if (QueryParser.LooksLikeGrammar(text, knownColumns))
            {
                try
                {
                    var parsed = QueryParser.Parse(text);
                    SetQueryError(_control, null);
                    Predicate<object>? predicate = null;
                    if (parsed?.Predicate is not null)
                    {
                        var compiled = QueryCompiler.Compile(parsed.Predicate, _columns, caseSensitive);
                        predicate = item => compiled(item);
                    }
                    _lastGoodInputPredicate = predicate;
                    return (predicate, parsed?.Order ?? Array.Empty<OrderSpec>());
                }
                catch (QueryException ex)
                {
                    SetQueryError(_control, ex.Message);
                    return (_lastGoodInputPredicate, Array.Empty<OrderSpec>());
                }
            }
            SetQueryError(_control, null);
            var bareTextPredicate = BuildBareTextPredicate(text!, caseSensitive);
            _lastGoodInputPredicate = bareTextPredicate;
            return (bareTextPredicate, Array.Empty<OrderSpec>());
        }

        private Predicate<object>? BuildBareTextPredicate(string text, bool caseSensitive)
        {
            var cmp = caseSensitive ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase;
            var needle = text.Trim();
            if (needle.Length == 0) return null;
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

        private static Type? InferItemType(IEnumerable source)
        {
            var type = source.GetType();
            foreach (var iface in type.GetInterfaces())
            {
                if (iface.IsGenericType && iface.GetGenericTypeDefinition() == typeof(IEnumerable<>))
                {
                    return iface.GetGenericArguments()[0];
                }
            }
            foreach (var item in source)
            {
                return item?.GetType();
            }
            return null;
        }
    }
}
