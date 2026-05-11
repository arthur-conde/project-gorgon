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

    public event PropertyChangedEventHandler? PropertyChanged;
    public event EventHandler? PredicateChanged;

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

    private void Recompile()
    {
        if (string.IsNullOrWhiteSpace(_queryText))
        {
            Error = null;
            Predicate = null;
            return;
        }
        try
        {
            var compiled = QueryCompiler.Compile(_queryText!, _bindings, _caseSensitive);
            Error = null;
            Predicate = compiled is null ? null : item => compiled(item);
        }
        catch (QueryException ex)
        {
            Error = ex.Message;
            // Intentionally keep last-good Predicate so the UI doesn't flicker
            // mid-typing; the error string is the signal that the live query
            // didn't take effect.
        }
    }

    private void OnPropertyChanged([CallerMemberName] string? property = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(property));
}
