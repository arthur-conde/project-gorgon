using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Runtime.CompilerServices;

namespace Mithril.Shared.Wpf.Query;

/// <summary>
/// VM-side helper that turns a <c>QueryText</c> string into a typed
/// <see cref="Predicate"/> against <typeparamref name="T"/> using the same
/// query grammar as <c>MithrilQueryBox</c>/<c>MithrilDataGrid</c>. Owns its
/// schema (reflected from <typeparamref name="T"/>'s public properties by
/// default) and surfaces parse/compile errors so a view can display them.
/// </summary>
/// <remarks>
/// <para>
/// Unlike the <c>QueryFilter</c> attached behavior (which composes onto an
/// <c>ICollectionView</c>), this helper is plain CLR — no WPF references — so
/// it suits VMs that filter, group, and project a source list themselves
/// (e.g. <c>Celebrimbor.AugmentPoolViewModel</c>) and any headless test.
/// </para>
/// <para>
/// On a parse failure the last successfully compiled <see cref="Predicate"/>
/// is retained, mirroring <c>MithrilDataGrid</c>'s "last-good" behaviour, so
/// the visible row set doesn't flicker while the user is mid-typing.
/// </para>
/// </remarks>
public sealed class QueryableSource<T> : INotifyPropertyChanged where T : class
{
    private readonly IReadOnlyDictionary<string, ColumnBinding> _bindings;
    private string? _queryText;
    private bool _caseSensitive;
    private Func<T, bool>? _predicate;
    private IReadOnlyList<OrderSpec> _order = Array.Empty<OrderSpec>();
    private string? _error;

    public QueryableSource(bool caseSensitive = false)
        : this(ColumnBindingHelper.BuildFromProperties(typeof(T)), caseSensitive)
    {
    }

    public QueryableSource(IReadOnlyDictionary<string, ColumnBinding> columns, bool caseSensitive = false)
    {
        _bindings = columns ?? throw new ArgumentNullException(nameof(columns));
        Schema = ColumnBindingHelper.ToSchema(_bindings);
        _caseSensitive = caseSensitive;
    }

    public IReadOnlyList<ColumnSchema> Schema { get; }

    public string? QueryText
    {
        get => _queryText;
        set
        {
            if (string.Equals(_queryText, value, StringComparison.Ordinal)) return;
            _queryText = value;
            OnPropertyChanged();
            Recompile();
        }
    }

    public bool CaseSensitive
    {
        get => _caseSensitive;
        set
        {
            if (_caseSensitive == value) return;
            _caseSensitive = value;
            OnPropertyChanged();
            Recompile();
        }
    }

    /// <summary>
    /// Last parse/compile error message, or <c>null</c> when the current
    /// <see cref="QueryText"/> parsed cleanly (including empty/whitespace).
    /// </summary>
    public string? Error
    {
        get => _error;
        private set
        {
            if (string.Equals(_error, value, StringComparison.Ordinal)) return;
            _error = value;
            OnPropertyChanged();
        }
    }

    /// <summary>
    /// Compiled predicate, or <c>null</c> when <see cref="QueryText"/> is
    /// empty. On a parse failure this retains the previously good predicate
    /// (see class remarks); inspect <see cref="Error"/> to detect that case.
    /// </summary>
    public Func<T, bool>? Predicate
    {
        get => _predicate;
        private set
        {
            if (ReferenceEquals(_predicate, value)) return;
            _predicate = value;
            OnPropertyChanged();
            PredicateChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    /// <summary>
    /// Parsed ORDER BY clause for the current <see cref="QueryText"/>, or
    /// empty when the query has no sort clause. Empty also when parsing fails
    /// (see <see cref="Error"/>).
    /// </summary>
    public IReadOnlyList<OrderSpec> Order
    {
        get => _order;
        private set
        {
            if (ReferenceEquals(_order, value)) return;
            _order = value;
            OnPropertyChanged();
            OrderChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    public event PropertyChangedEventHandler? PropertyChanged;
    public event EventHandler? PredicateChanged;
    public event EventHandler? OrderChanged;

    /// <summary>
    /// Convenience: filter <paramref name="source"/> by the current
    /// <see cref="Predicate"/>, or pass through unchanged when no query is set.
    /// </summary>
    public IEnumerable<T> Apply(IEnumerable<T> source)
    {
        ArgumentNullException.ThrowIfNull(source);
        var p = _predicate;
        return p is null ? source : source.Where(p);
    }

    /// <summary>
    /// Filter <paramref name="source"/> by the current <see cref="Predicate"/>
    /// (if any) and then sort by the current <see cref="Order"/>. When no order
    /// clause is set, the filtered enumerable is returned in iteration order.
    /// </summary>
    public IEnumerable<T> ApplyOrdered(IEnumerable<T> source)
    {
        ArgumentNullException.ThrowIfNull(source);
        var filtered = Apply(source);
        if (_order.Count == 0)
        {
            return filtered;
        }
        IOrderedEnumerable<T>? ordered = null;
        for (int i = 0; i < _order.Count; i++)
        {
            var spec = _order[i];
            // ResolveColumn returns the canonical binding; resolution is case-insensitive
            // unless _caseSensitive is true (matches QueryCompiler).
            var key = _bindings[spec.Column];
            Func<T, object?> selector = item => key.GetValue(item!);
            if (ordered is null)
            {
                ordered = spec.Direction == OrderDirection.Ascending
                    ? filtered.OrderBy(selector, NullSafeComparer.Instance)
                    : filtered.OrderByDescending(selector, NullSafeComparer.Instance);
            }
            else
            {
                ordered = spec.Direction == OrderDirection.Ascending
                    ? ordered.ThenBy(selector, NullSafeComparer.Instance)
                    : ordered.ThenByDescending(selector, NullSafeComparer.Instance);
            }
        }
        return ordered!;
    }

    private void Recompile()
    {
        if (string.IsNullOrWhiteSpace(_queryText))
        {
            Error = null;
            Predicate = null;
            Order = Array.Empty<OrderSpec>();
            return;
        }
        try
        {
            var parsed = QueryParser.Parse(_queryText!);
            if (parsed is null)
            {
                Error = null;
                Predicate = null;
                Order = Array.Empty<OrderSpec>();
                return;
            }
            Func<T, bool>? predicate = null;
            if (parsed.Predicate is not null)
            {
                var compiled = QueryCompiler.Compile(parsed.Predicate, _bindings, _caseSensitive);
                predicate = item => compiled(item);
            }
            // Compile order eagerly so unknown-column errors surface here, not at Apply time.
            _ = QueryCompiler.CompileOrder(parsed.Order, _bindings, _caseSensitive);
            Error = null;
            Predicate = predicate;
            Order = parsed.Order;
        }
        catch (QueryException ex)
        {
            Error = ex.Message;
            // Keep last-good Predicate + Order so UI doesn't flicker mid-typing.
        }
    }

    private void OnPropertyChanged([CallerMemberName] string? property = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(property));

    private sealed class NullSafeComparer : IComparer<object?>
    {
        public static readonly NullSafeComparer Instance = new();
        public int Compare(object? x, object? y)
        {
            if (ReferenceEquals(x, y)) return 0;
            if (x is null) return -1;
            if (y is null) return 1;
            if (x is IComparable xc && x.GetType() == y.GetType()) return xc.CompareTo(y);
            return string.Compare(
                System.Convert.ToString(x, System.Globalization.CultureInfo.InvariantCulture),
                System.Convert.ToString(y, System.Globalization.CultureInfo.InvariantCulture),
                System.StringComparison.OrdinalIgnoreCase);
        }
    }
}
