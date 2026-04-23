using System;
using System.Collections.Generic;
using System.Linq;

namespace Gorgon.Shared.Wpf.Query;

public enum CompletionKind
{
    Column,
    Keyword,
    Operator,
    Value,
}

/// <summary>
/// One suggestion from <see cref="QueryCompletionProvider"/>.
/// </summary>
/// <param name="Label">Text shown in the popup.</param>
/// <param name="InsertText">Exact text to substitute for <see cref="ReplaceStart"/>..<see cref="ReplaceEnd"/>.</param>
/// <param name="ReplaceStart">Start index in the original query string to replace.</param>
/// <param name="ReplaceEnd">Exclusive end index in the original query string to replace.</param>
/// <param name="Kind">Category used for icon/coloring in the UI.</param>
/// <param name="Detail">Optional right-hand hint (e.g. column type).</param>
public sealed record CompletionItem(
    string Label,
    string InsertText,
    int ReplaceStart,
    int ReplaceEnd,
    CompletionKind Kind,
    string? Detail = null);

/// <summary>
/// Schema information for a column, used to tailor operator + value suggestions.
/// </summary>
/// <param name="Name">Column name matched by the grammar.</param>
/// <param name="ValueType">Property type — e.g. <c>typeof(string)</c>, <c>typeof(int)</c>, <c>typeof(TimeSpan)</c>.</param>
/// <param name="IsNullable">Whether the column value may be null (adds IS NULL / IS NOT NULL to operators).</param>
public sealed record ColumnSchema(string Name, Type ValueType, bool IsNullable);

public static class QueryCompletionProvider
{
    private static readonly string[] AllComparisonOps = { "=", "!=", "<", "<=", ">", ">=" };

    public static IReadOnlyList<CompletionItem> Suggest(
        string query,
        int caret,
        IReadOnlyList<ColumnSchema> columns,
        Func<string, IReadOnlyList<string>>? valueSampler = null)
    {
        query ??= string.Empty;
        if (caret < 0) caret = 0;
        if (caret > query.Length) caret = query.Length;

        var tokens = QueryParser.LexPermissive(query);
        // The last non-EOF token strictly before the caret. "Strictly before" means
        // its end position is <= caret, so typing adjacent to a keyword still suggests
        // things that *follow* it.
        var context = BuildContext(query, caret, tokens, columns);

        var results = new List<CompletionItem>();
        switch (context.Expecting)
        {
            case Expecting.ColumnOrBool:
                AddColumnSuggestions(results, context, columns);
                // NOT can also begin a predicate — allow it at the top.
                AddKeywordIfStartsWith(results, "NOT", context);
                break;
            case Expecting.Operator:
                AddOperatorSuggestions(results, context, context.CurrentColumn);
                break;
            case Expecting.Value:
                AddValueSuggestions(results, context, context.CurrentColumn, valueSampler);
                break;
            case Expecting.AndConnector:
                AddKeywordIfStartsWith(results, "AND", context);
                break;
            case Expecting.Combinator:
                AddKeywordIfStartsWith(results, "AND", context);
                AddKeywordIfStartsWith(results, "OR", context);
                break;
        }

        // Filter by the prefix the user has already typed, preserving insertion point.
        var prefix = context.PartialText;
        if (!string.IsNullOrEmpty(prefix))
        {
            results = results
                .Where(r => r.Label.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                .ToList();
        }

        return results;
    }

    // ─────────────── context inference ───────────────

    internal enum Expecting
    {
        ColumnOrBool,   // start / after ( / after AND / OR / NOT
        Operator,       // after an identifier that resolved to a column
        Value,          // after a comparison op / LIKE / between-value / IN-value
        AndConnector,   // between the low and high of a BETWEEN
        Combinator,     // after a complete predicate — AND / OR
    }

    internal readonly record struct Context(
        Expecting Expecting,
        string PartialText,
        int ReplaceStart,
        int ReplaceEnd,
        ColumnSchema? CurrentColumn);

    private static Context BuildContext(
        string query, int caret, IReadOnlyList<QueryParser.Token> tokens, IReadOnlyList<ColumnSchema> columns)
    {
        // Determine the partial token under the caret (if any): the token whose
        // [start, end) range contains `caret`, OR an adjacent identifier being
        // typed into. If caret is past the end of all tokens, treat as empty.
        var (partialToken, partialStart, partialEnd) = FindPartial(query, caret, tokens);

        // Tokens strictly before the caret (excluding the partial one).
        var effective = tokens
            .Where(t => t.Kind != QueryParser.TokenKind.Eof)
            .Where(t => t.Position + EffectiveLength(t, query) <= caret && !(partialToken.HasValue && t.Position == partialToken.Value.Position))
            .ToList();

        var (expecting, currentColumn) = ClassifyExpecting(effective, columns);
        string partialText = partialEnd > partialStart ? query.Substring(partialStart, partialEnd - partialStart) : string.Empty;
        return new Context(expecting, partialText, partialStart, partialEnd, currentColumn);
    }

    private static int EffectiveLength(QueryParser.Token t, string query)
    {
        // String tokens store their decoded payload; on-screen width includes quotes.
        if (t.Kind != QueryParser.TokenKind.String)
        {
            return t.Text.Length;
        }
        int end = t.Position;
        if (end >= query.Length) return 0;
        char quote = query[end];
        end++;
        while (end < query.Length)
        {
            if (query[end] == quote)
            {
                if (end + 1 < query.Length && query[end + 1] == quote)
                {
                    end += 2; continue;
                }
                end++;
                break;
            }
            end++;
        }
        return end - t.Position;
    }

    private static (QueryParser.Token? Token, int Start, int End) FindPartial(
        string query, int caret, IReadOnlyList<QueryParser.Token> tokens)
    {
        foreach (var t in tokens)
        {
            if (t.Kind == QueryParser.TokenKind.Eof) continue;
            int end = t.Position + EffectiveLength(t, query);
            if (t.Position <= caret && caret <= end)
            {
                // Only identifiers count as a "partial prefix" we should filter by —
                // for strings/operators/punct we don't replace the token.
                if (t.Kind == QueryParser.TokenKind.Identifier
                    || IsKeywordKind(t.Kind))
                {
                    return (t, t.Position, end);
                }
                return (null, caret, caret);
            }
        }
        return (null, caret, caret);
    }

    private static bool IsKeywordKind(QueryParser.TokenKind k) =>
        k is QueryParser.TokenKind.And or QueryParser.TokenKind.Or or QueryParser.TokenKind.Not
          or QueryParser.TokenKind.Like or QueryParser.TokenKind.In or QueryParser.TokenKind.Between
          or QueryParser.TokenKind.Is or QueryParser.TokenKind.Null
          or QueryParser.TokenKind.True or QueryParser.TokenKind.False;

    private static (Expecting, ColumnSchema?) ClassifyExpecting(
        List<QueryParser.Token> effective, IReadOnlyList<ColumnSchema> columns)
    {
        if (effective.Count == 0)
        {
            return (Expecting.ColumnOrBool, null);
        }

        var last = effective[^1];
        var secondLast = effective.Count >= 2 ? effective[^2] : default;

        // After a value: we're between predicates, expect AND/OR (or ), or end).
        // Values are: String, Number, Duration, Null, True, False, RParen.
        if (IsValueEnding(last.Kind))
        {
            // Special case: if we're inside a BETWEEN (matched "BETWEEN <val>"), expect AND.
            if (HasTrailingBetweenLow(effective))
            {
                return (Expecting.AndConnector, null);
            }
            return (Expecting.Combinator, null);
        }

        // After AND / OR / NOT / (: expect a column.
        if (last.Kind is QueryParser.TokenKind.And or QueryParser.TokenKind.Or or QueryParser.TokenKind.Not or QueryParser.TokenKind.LParen)
        {
            return (Expecting.ColumnOrBool, null);
        }

        // After an operator: expect a value.
        if (IsComparisonOp(last.Kind)
            || last.Kind is QueryParser.TokenKind.Like or QueryParser.TokenKind.Contains
                         or QueryParser.TokenKind.StartsWith or QueryParser.TokenKind.EndsWith
                         or QueryParser.TokenKind.Comma)
        {
            var col = LookupColumn(FindLastColumnBeforeOperator(effective), columns);
            return (Expecting.Value, col);
        }
        if (last.Kind == QueryParser.TokenKind.In)
        {
            return (Expecting.Value, null); // user still needs to type `(`; we can't help much
        }
        if (last.Kind == QueryParser.TokenKind.Between)
        {
            return (Expecting.Value, LookupColumn(FindLastColumnBeforeOperator(effective), columns));
        }

        // After IS: expect NULL or NOT NULL (special-cased as keyword suggestions).
        // For now, no dedicated Expecting — fall through to combinator.
        if (last.Kind == QueryParser.TokenKind.Is)
        {
            return (Expecting.Combinator, null); // typing will just keyword-filter
        }

        // Identifier — we just completed a column name; expect an operator.
        if (last.Kind == QueryParser.TokenKind.Identifier)
        {
            var col = LookupColumn(last.Text, columns);
            return (Expecting.Operator, col);
        }

        return (Expecting.ColumnOrBool, null);
    }

    private static bool IsValueEnding(QueryParser.TokenKind k) =>
        k is QueryParser.TokenKind.String or QueryParser.TokenKind.Number
          or QueryParser.TokenKind.Duration or QueryParser.TokenKind.Null
          or QueryParser.TokenKind.True or QueryParser.TokenKind.False
          or QueryParser.TokenKind.RParen;

    private static bool IsComparisonOp(QueryParser.TokenKind k) =>
        k is QueryParser.TokenKind.Eq or QueryParser.TokenKind.Neq or QueryParser.TokenKind.Lt
          or QueryParser.TokenKind.Lte or QueryParser.TokenKind.Gt or QueryParser.TokenKind.Gte;

    private static string? FindLastColumnBeforeOperator(List<QueryParser.Token> effective)
    {
        // Walk backwards past the operator chain; the first Identifier we hit is the column.
        for (int i = effective.Count - 2; i >= 0; i--) // -2 skips the "last" token
        {
            if (effective[i].Kind == QueryParser.TokenKind.Identifier)
            {
                return effective[i].Text;
            }
        }
        return null;
    }

    private static bool HasTrailingBetweenLow(List<QueryParser.Token> effective)
    {
        // Pattern: Identifier BETWEEN value. We're AT the value, need AND next.
        for (int i = effective.Count - 1; i >= 0; i--)
        {
            if (effective[i].Kind == QueryParser.TokenKind.Between)
            {
                return true;
            }
            if (effective[i].Kind == QueryParser.TokenKind.And)
            {
                return false; // already past the "AND high" piece
            }
        }
        return false;
    }

    private static ColumnSchema? LookupColumn(string? name, IReadOnlyList<ColumnSchema> columns)
    {
        if (string.IsNullOrEmpty(name)) return null;
        foreach (var c in columns)
        {
            if (string.Equals(c.Name, name, StringComparison.OrdinalIgnoreCase))
            {
                return c;
            }
        }
        return null;
    }

    // ─────────────── emitters ───────────────

    private static void AddColumnSuggestions(
        List<CompletionItem> results, Context ctx, IReadOnlyList<ColumnSchema> columns)
    {
        foreach (var c in columns)
        {
            results.Add(new CompletionItem(
                c.Name,
                c.Name,
                ctx.ReplaceStart,
                ctx.ReplaceEnd,
                CompletionKind.Column,
                FormatType(c)));
        }
    }

    private static void AddOperatorSuggestions(List<CompletionItem> results, Context ctx, ColumnSchema? col)
    {
        var underlying = col is null ? null : Nullable.GetUnderlyingType(col.ValueType) ?? col.ValueType;
        var isString = underlying == typeof(string);
        var isBool = underlying == typeof(bool);
        var isNullable = col?.IsNullable == true || underlying != col?.ValueType;

        if (isString)
        {
            // `CONTAINS` / `STARTSWITH` / `ENDSWITH` listed first — they're the
            // friendliest form (plain text RHS, no wildcards to remember).
            Add(results, ctx, "CONTAINS", CompletionKind.Keyword);
            Add(results, ctx, "NOT CONTAINS", CompletionKind.Keyword);
            Add(results, ctx, "STARTSWITH", CompletionKind.Keyword);
            Add(results, ctx, "ENDSWITH", CompletionKind.Keyword);
            Add(results, ctx, "=", CompletionKind.Operator);
            Add(results, ctx, "!=", CompletionKind.Operator);
            Add(results, ctx, "LIKE", CompletionKind.Keyword);
            Add(results, ctx, "NOT LIKE", CompletionKind.Keyword);
            Add(results, ctx, "IN", CompletionKind.Keyword);
            Add(results, ctx, "NOT IN", CompletionKind.Keyword);
        }
        else if (isBool)
        {
            Add(results, ctx, "=", CompletionKind.Operator);
            Add(results, ctx, "!=", CompletionKind.Operator);
        }
        else
        {
            foreach (var op in AllComparisonOps)
            {
                Add(results, ctx, op, CompletionKind.Operator);
            }
            var isDateLike = underlying == typeof(DateTime)
                          || underlying == typeof(DateTimeOffset)
                          || underlying == typeof(TimeSpan);
            if (isDateLike)
            {
                Add(results, ctx, "BEFORE", CompletionKind.Keyword);
                Add(results, ctx, "AFTER", CompletionKind.Keyword);
            }
            Add(results, ctx, "BETWEEN", CompletionKind.Keyword);
            Add(results, ctx, "NOT BETWEEN", CompletionKind.Keyword);
            Add(results, ctx, "IN", CompletionKind.Keyword);
            Add(results, ctx, "NOT IN", CompletionKind.Keyword);
        }

        if (isNullable)
        {
            Add(results, ctx, "IS NULL", CompletionKind.Keyword);
            Add(results, ctx, "IS NOT NULL", CompletionKind.Keyword);
        }
    }

    private static void AddValueSuggestions(
        List<CompletionItem> results, Context ctx, ColumnSchema? col,
        Func<string, IReadOnlyList<string>>? valueSampler)
    {
        if (col is null)
        {
            return;
        }
        var underlying = Nullable.GetUnderlyingType(col.ValueType) ?? col.ValueType;
        if (underlying == typeof(bool))
        {
            Add(results, ctx, "TRUE", CompletionKind.Keyword);
            Add(results, ctx, "FALSE", CompletionKind.Keyword);
            return;
        }
        if (underlying == typeof(string) && valueSampler is not null)
        {
            foreach (var sample in valueSampler(col.Name))
            {
                var quoted = "'" + sample.Replace("'", "''") + "'";
                results.Add(new CompletionItem(
                    quoted,
                    quoted,
                    ctx.ReplaceStart,
                    ctx.ReplaceEnd,
                    CompletionKind.Value,
                    null));
            }
            return;
        }
        if (underlying == typeof(DateTime) || underlying == typeof(DateTimeOffset))
        {
            Add(results, ctx, "NOW()", CompletionKind.Value);
            Add(results, ctx, "TODAY()", CompletionKind.Value);
        }
        // Numeric / duration: no value suggestions — format hints could go here.
    }

    private static void Add(List<CompletionItem> list, Context ctx, string text, CompletionKind kind)
    {
        list.Add(new CompletionItem(text, text, ctx.ReplaceStart, ctx.ReplaceEnd, kind));
    }

    private static void AddKeywordIfStartsWith(List<CompletionItem> list, string keyword, Context ctx)
    {
        list.Add(new CompletionItem(keyword, keyword, ctx.ReplaceStart, ctx.ReplaceEnd, CompletionKind.Keyword));
    }

    private static string FormatType(ColumnSchema c)
    {
        var t = Nullable.GetUnderlyingType(c.ValueType) ?? c.ValueType;
        return c.IsNullable ? $"{t.Name}?" : t.Name;
    }
}
