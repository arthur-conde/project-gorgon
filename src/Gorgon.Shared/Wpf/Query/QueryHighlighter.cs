using System.Collections.Generic;

namespace Gorgon.Shared.Wpf.Query;

public enum HighlightKind
{
    Column,
    UnknownColumn,
    Keyword,
    Operator,
    String,
    Number,
    Duration,
    Punct,
    Error,
}

public readonly record struct HighlightSpan(int Start, int Length, HighlightKind Kind);

public static class QueryHighlighter
{
    public static IReadOnlyList<HighlightSpan> Highlight(string query, IReadOnlySet<string> knownColumns)
    {
        var spans = new List<HighlightSpan>();
        if (string.IsNullOrEmpty(query))
        {
            return spans;
        }

        var tokens = QueryParser.LexPermissive(query);
        foreach (var token in tokens)
        {
            if (token.Kind == QueryParser.TokenKind.Eof)
            {
                break;
            }
            int length = TokenLength(token, query);
            if (length <= 0)
            {
                continue;
            }
            var kind = Classify(token, knownColumns);
            spans.Add(new HighlightSpan(token.Position, length, kind));
        }
        return spans;
    }

    private static int TokenLength(QueryParser.Token token, string query)
    {
        // String tokens store the decoded content; the on-screen length includes quotes.
        if (token.Kind == QueryParser.TokenKind.String)
        {
            int end = token.Position;
            if (end >= query.Length)
            {
                return 0;
            }
            char quote = query[end];
            end++;
            while (end < query.Length)
            {
                if (query[end] == quote)
                {
                    if (end + 1 < query.Length && query[end + 1] == quote)
                    {
                        end += 2;
                        continue;
                    }
                    end++;
                    break;
                }
                end++;
            }
            return end - token.Position;
        }
        return token.Text.Length;
    }

    private static HighlightKind Classify(QueryParser.Token token, IReadOnlySet<string> knownColumns) => token.Kind switch
    {
        QueryParser.TokenKind.Identifier => knownColumns.Contains(token.Text)
            ? HighlightKind.Column
            : HighlightKind.UnknownColumn,
        QueryParser.TokenKind.String => HighlightKind.String,
        QueryParser.TokenKind.Number => HighlightKind.Number,
        QueryParser.TokenKind.Duration => HighlightKind.Duration,
        QueryParser.TokenKind.LParen or QueryParser.TokenKind.RParen or QueryParser.TokenKind.Comma => HighlightKind.Punct,
        QueryParser.TokenKind.Eq or QueryParser.TokenKind.Neq or QueryParser.TokenKind.Lt
            or QueryParser.TokenKind.Lte or QueryParser.TokenKind.Gt or QueryParser.TokenKind.Gte => HighlightKind.Operator,
        QueryParser.TokenKind.And or QueryParser.TokenKind.Or or QueryParser.TokenKind.Not
            or QueryParser.TokenKind.Like or QueryParser.TokenKind.In or QueryParser.TokenKind.Between
            or QueryParser.TokenKind.Is or QueryParser.TokenKind.Null
            or QueryParser.TokenKind.True or QueryParser.TokenKind.False => HighlightKind.Keyword,
        QueryParser.TokenKind.Error => HighlightKind.Error,
        _ => HighlightKind.Error,
    };
}
