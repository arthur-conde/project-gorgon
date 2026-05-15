using System;
using System.Collections;
using System.Collections.Generic;

namespace Mithril.Shared.Wpf.Query;

/// <summary>
/// Composite <see cref="IComparer"/> built from a parsed <c>ORDER BY</c> clause.
/// Walks the keys left-to-right, picking <see cref="NaturalStringComparer"/> for
/// string-typed columns and <see cref="Comparer{T}.Default"/> for everything
/// else (numerics, <c>DateTime</c>, <c>TimeSpan</c>, enums — anything
/// <see cref="IComparable"/>). Direction is applied per-key by negation.
/// </summary>
/// <remarks>
/// <para>
/// The non-generic <see cref="IComparer"/> shape is what
/// <see cref="System.Windows.Data.ListCollectionView.CustomSort"/> requires.
/// <c>QueryableSource&lt;T&gt;.ApplyOrdered</c> consumes the same instance via
/// <c>IEnumerable.OrderBy(x =&gt; (object)x, comparer)</c> so both surfaces share
/// one implementation.
/// </para>
/// <para>
/// Null-safe: a <c>null</c> key value sorts less than any non-null in ASC
/// (matching the prior <c>QueryableSource.NullSafeComparer</c> contract).
/// Empty order list yields a no-op comparer that returns 0 for any pair.
/// </para>
/// </remarks>
internal sealed class OrderComparer : IComparer, IComparer<object>
{
    private readonly Key[] _keys;

    public OrderComparer(IReadOnlyList<OrderSpec> order, IReadOnlyDictionary<string, ColumnBinding> columns, bool caseSensitive)
    {
        var stringCmp = caseSensitive ? NaturalStringComparer.Ordinal : NaturalStringComparer.OrdinalIgnoreCase;
        _keys = new Key[order.Count];
        for (int i = 0; i < order.Count; i++)
        {
            var spec = order[i];
            var binding = columns[spec.Column];
            var underlying = Nullable.GetUnderlyingType(binding.ValueType) ?? binding.ValueType;
            _keys[i] = new Key(
                binding.GetValue,
                spec.Direction == OrderDirection.Descending ? -1 : 1,
                underlying == typeof(string) ? (IComparer)stringCmp : Comparer<object>.Default);
        }
    }

    public int Compare(object? x, object? y)
    {
        if (ReferenceEquals(x, y)) return 0;
        // Top-level nulls are not expected from ICollectionView (it iterates non-null items)
        // but the contract is symmetric.
        if (x is null) return -1;
        if (y is null) return 1;

        for (int i = 0; i < _keys.Length; i++)
        {
            var key = _keys[i];
            var a = key.GetValue(x);
            var b = key.GetValue(y);
            int cmp;
            if (a is null)
            {
                cmp = b is null ? 0 : -1;
            }
            else if (b is null)
            {
                cmp = 1;
            }
            else
            {
                cmp = key.Comparer.Compare(a, b);
            }
            if (cmp != 0) return cmp * key.Sign;
        }
        return 0;
    }

    private readonly record struct Key(Func<object, object?> GetValue, int Sign, IComparer Comparer);
}
