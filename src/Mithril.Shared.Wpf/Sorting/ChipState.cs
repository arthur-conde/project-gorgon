using System;
using System.Collections.Generic;
using Mithril.Shared.Wpf.Query;

namespace Mithril.Shared.Wpf.Sorting;

/// <summary>
/// Derived view of one available <see cref="SortKey{T}"/> against the
/// currently-parsed <see cref="OrderSpec"/> list. Chips bind to this; the
/// stored model is the query text, this projection is recomputed each parse.
/// </summary>
public sealed record ChipState<T>(
    SortKey<T> Key,
    bool IsActive,
    OrderDirection? Direction,
    int OrderIndex);

public static class ChipState
{
    public static IReadOnlyList<ChipState<T>> Project<T>(
        IReadOnlyList<SortKey<T>> available,
        IReadOnlyList<OrderSpec> parsedOrder)
    {
        ArgumentNullException.ThrowIfNull(available);
        ArgumentNullException.ThrowIfNull(parsedOrder);
        var result = new List<ChipState<T>>(available.Count);
        foreach (var key in available)
        {
            int index = -1;
            OrderDirection? dir = null;
            for (int i = 0; i < parsedOrder.Count; i++)
            {
                if (string.Equals(parsedOrder[i].Column, key.Id, StringComparison.OrdinalIgnoreCase))
                {
                    index = i;
                    dir = parsedOrder[i].Direction;
                    break;
                }
            }
            result.Add(new ChipState<T>(key, index >= 0, dir, index));
        }
        return result;
    }
}
