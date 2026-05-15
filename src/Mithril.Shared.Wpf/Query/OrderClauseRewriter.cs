using System;
using System.Collections.Generic;
using System.Text;

namespace Mithril.Shared.Wpf.Query;

/// <summary>
/// Rewrites the <c>ORDER BY</c> segment of a query string while preserving the
/// predicate portion verbatim (including its whitespace). Used by chip and
/// column-header click handlers to keep the query-box text the canonical
/// source of truth for the sort plan.
/// </summary>
public static class OrderClauseRewriter
{
    public static string Rewrite(string? input, IReadOnlyList<OrderSpec> newOrder)
    {
        ArgumentNullException.ThrowIfNull(newOrder);
        var predicate = StripOrderClause(input ?? string.Empty);
        var clause = FormatOrderClause(newOrder);

        if (clause.Length == 0)
        {
            // Trim only the trailing whitespace that was sitting between
            // predicate and the now-gone ORDER BY; leave leading whitespace alone.
            return predicate.TrimEnd();
        }
        if (predicate.Length == 0)
        {
            return clause;
        }
        // Preserve the existing whitespace between predicate and ORDER BY when there
        // is any; otherwise insert a single space separator.
        if (char.IsWhiteSpace(predicate[^1]))
        {
            return predicate + clause;
        }
        return predicate + " " + clause;
    }

    /// <summary>
    /// Format an order list as "ORDER BY Col [DESC][, Col [DESC]]...". Empty
    /// list returns the empty string. ASC is implicit (omitted).
    /// </summary>
    public static string FormatOrderClause(IReadOnlyList<OrderSpec> order)
    {
        if (order.Count == 0) return string.Empty;
        var sb = new StringBuilder("ORDER BY ");
        for (int i = 0; i < order.Count; i++)
        {
            if (i > 0) sb.Append(", ");
            sb.Append(order[i].Column);
            if (order[i].Direction == OrderDirection.Descending)
            {
                sb.Append(" DESC");
            }
        }
        return sb.ToString();
    }

    /// <summary>
    /// Return the input with any trailing ORDER BY / SORT BY clause removed.
    /// Locates the clause by scanning tokens via <see cref="QueryParser.LexPermissive"/>
    /// so quoted strings and nested parentheses don't trip the search.
    /// </summary>
    private static string StripOrderClause(string input)
    {
        if (string.IsNullOrEmpty(input)) return input;
        var tokens = QueryParser.LexPermissive(input);
        foreach (var t in tokens)
        {
            if (t.Kind == QueryParser.TokenKind.OrderBy || t.Kind == QueryParser.TokenKind.SortBy)
            {
                return input[..t.Position];
            }
        }
        return input;
    }
}
