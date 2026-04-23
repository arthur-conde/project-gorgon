using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace Gorgon.Shared.Wpf.Query;

public static class QueryParser
{
    public static QueryNode? Parse(string query)
    {
        if (string.IsNullOrWhiteSpace(query))
        {
            return null;
        }

        var tokens = Lex(query);
        var parser = new Parser(tokens);
        var node = parser.ParseExpression();
        parser.ExpectEof();
        return node;
    }

    private static readonly HashSet<string> UppercaseKeywords = new(StringComparer.Ordinal)
    {
        "AND", "OR", "NOT", "LIKE", "IN", "BETWEEN", "IS", "NULL", "TRUE", "FALSE",
        "BEFORE", "AFTER",
        "CONTAINS", "STARTSWITH", "ENDSWITH",
    };

    /// <summary>
    /// Cheap classifier for "is this input intended as grammar vs plain search text?"
    /// True if the input contains any operator/punctuation character, an uppercase
    /// reserved word as a whole token, or (when <paramref name="knownColumns"/> is
    /// supplied) a token matching a known column name case-insensitively. Keywords
    /// themselves MUST be uppercase — so common English words like <c>not</c>,
    /// <c>is</c>, <c>or</c> don't trigger a grammar classification — but column
    /// names match regardless of case so a user typing <c>CropType LIK</c> is
    /// already considered to be composing a query.
    /// </summary>
    public static bool LooksLikeGrammar(string? query, IReadOnlySet<string>? knownColumns = null)
    {
        if (string.IsNullOrWhiteSpace(query))
        {
            return false;
        }
        foreach (char c in query)
        {
            if (c == '=' || c == '<' || c == '>' || c == '!' || c == '(' || c == ')' || c == ',' || c == '\'' || c == '"')
            {
                return true;
            }
        }
        int i = 0;
        while (i < query!.Length)
        {
            if (!IsIdentStart(query[i]))
            {
                i++;
                continue;
            }
            int start = i;
            while (i < query.Length && IsIdentPart(query[i]))
            {
                i++;
            }
            var word = query[start..i];
            if (UppercaseKeywords.Contains(word))
            {
                return true;
            }
            if (knownColumns is not null && knownColumns.Contains(word))
            {
                return true;
            }
        }
        return false;
    }

    // ───────────────────────── Lexer ─────────────────────────

    internal enum TokenKind
    {
        Identifier,
        String,
        Number,
        Duration,
        LParen,
        RParen,
        Comma,
        Eq,
        Neq,
        Lt,
        Lte,
        Gt,
        Gte,
        And,
        Or,
        Not,
        Like,
        Contains,
        StartsWith,
        EndsWith,
        In,
        Between,
        Is,
        Null,
        True,
        False,
        Error,
        Eof,
    }

    internal readonly record struct Token(TokenKind Kind, string Text, int Position, object? Payload = null);

    private static readonly Dictionary<string, TokenKind> Keywords = new(StringComparer.OrdinalIgnoreCase)
    {
        ["AND"] = TokenKind.And,
        ["OR"] = TokenKind.Or,
        ["NOT"] = TokenKind.Not,
        ["LIKE"] = TokenKind.Like,
        ["CONTAINS"] = TokenKind.Contains,
        ["STARTSWITH"] = TokenKind.StartsWith,
        ["ENDSWITH"] = TokenKind.EndsWith,
        ["IN"] = TokenKind.In,
        ["BETWEEN"] = TokenKind.Between,
        ["IS"] = TokenKind.Is,
        ["NULL"] = TokenKind.Null,
        ["TRUE"] = TokenKind.True,
        ["FALSE"] = TokenKind.False,
        // English-aliased comparison operators, most useful for DateTime columns
        // (`Timestamp BEFORE NOW()`) but work for any orderable type.
        ["BEFORE"] = TokenKind.Lt,
        ["AFTER"] = TokenKind.Gt,
    };

    internal static List<Token> Lex(string source) => LexCore(source, permissive: false);

    /// <summary>
    /// Permissive lex that never throws. Malformed fragments are emitted as
    /// <see cref="TokenKind.Error"/> tokens so live highlighting and completion
    /// can keep classifying the rest of the input.
    /// </summary>
    internal static IReadOnlyList<Token> LexPermissive(string source) => LexCore(source, permissive: true);

    private static List<Token> LexCore(string source, bool permissive)
    {
        var tokens = new List<Token>();
        int i = 0;
        while (i < source.Length)
        {
            char c = source[i];
            if (char.IsWhiteSpace(c))
            {
                i++;
                continue;
            }

            int start = i;

            if (c == '(')
            {
                tokens.Add(new Token(TokenKind.LParen, "(", start));
                i++;
                continue;
            }
            if (c == ')')
            {
                tokens.Add(new Token(TokenKind.RParen, ")", start));
                i++;
                continue;
            }
            if (c == ',')
            {
                tokens.Add(new Token(TokenKind.Comma, ",", start));
                i++;
                continue;
            }

            if (c == '=')
            {
                tokens.Add(new Token(TokenKind.Eq, "=", start));
                i++;
                continue;
            }
            if (c == '!' && i + 1 < source.Length && source[i + 1] == '=')
            {
                tokens.Add(new Token(TokenKind.Neq, "!=", start));
                i += 2;
                continue;
            }
            if (c == '<')
            {
                if (i + 1 < source.Length && source[i + 1] == '=')
                {
                    tokens.Add(new Token(TokenKind.Lte, "<=", start));
                    i += 2;
                    continue;
                }
                if (i + 1 < source.Length && source[i + 1] == '>')
                {
                    tokens.Add(new Token(TokenKind.Neq, "<>", start));
                    i += 2;
                    continue;
                }
                tokens.Add(new Token(TokenKind.Lt, "<", start));
                i++;
                continue;
            }
            if (c == '>')
            {
                if (i + 1 < source.Length && source[i + 1] == '=')
                {
                    tokens.Add(new Token(TokenKind.Gte, ">=", start));
                    i += 2;
                    continue;
                }
                tokens.Add(new Token(TokenKind.Gt, ">", start));
                i++;
                continue;
            }

            if (c == '\'' || c == '"')
            {
                if (TryReadString(source, ref i, c, out var strTok))
                {
                    tokens.Add(strTok);
                }
                else if (permissive)
                {
                    // Unterminated string — emit it up to end of input as a String
                    // token so highlighting colors the partial literal correctly.
                    tokens.Add(new Token(TokenKind.String, source[(start + 1)..], start));
                    i = source.Length;
                }
                else
                {
                    throw new QueryException("Unterminated string literal.", start);
                }
                continue;
            }

            if (char.IsDigit(c) || (c == '-' && i + 1 < source.Length && char.IsDigit(source[i + 1])))
            {
                if (TryReadNumberOrDuration(source, ref i, out var numTok, out var error))
                {
                    tokens.Add(numTok);
                }
                else if (permissive)
                {
                    tokens.Add(new Token(TokenKind.Error, source[start..i], start));
                }
                else
                {
                    throw error!;
                }
                continue;
            }

            if (IsIdentStart(c))
            {
                int idStart = i;
                while (i < source.Length && IsIdentPart(source[i]))
                {
                    i++;
                }
                string text = source[idStart..i];
                if (Keywords.TryGetValue(text, out var kw))
                {
                    tokens.Add(new Token(kw, text, idStart));
                }
                else
                {
                    tokens.Add(new Token(TokenKind.Identifier, text, idStart));
                }
                continue;
            }

            if (permissive)
            {
                tokens.Add(new Token(TokenKind.Error, c.ToString(), i));
                i++;
                continue;
            }
            throw new QueryException($"Unexpected character '{c}'.", i);
        }

        tokens.Add(new Token(TokenKind.Eof, string.Empty, source.Length));
        return tokens;
    }

    private static bool TryReadString(string source, ref int i, char quote, out Token token)
    {
        int start = i;
        int cursor = i + 1;
        var sb = new StringBuilder();
        while (cursor < source.Length)
        {
            char ch = source[cursor];
            if (ch == quote)
            {
                if (cursor + 1 < source.Length && source[cursor + 1] == quote)
                {
                    sb.Append(quote);
                    cursor += 2;
                    continue;
                }
                i = cursor + 1;
                token = new Token(TokenKind.String, sb.ToString(), start);
                return true;
            }
            sb.Append(ch);
            cursor++;
        }
        token = default;
        return false;
    }

    private static bool TryReadNumberOrDuration(string source, ref int i, out Token token, out QueryException? error)
    {
        int start = i;
        bool negative = false;
        int cursor = i;
        if (source[cursor] == '-')
        {
            negative = true;
            cursor++;
        }

        int firstDigitStart = cursor;
        while (cursor < source.Length && char.IsDigit(source[cursor]))
        {
            cursor++;
        }

        if (cursor < source.Length && source[cursor] == '.')
        {
            cursor++;
            while (cursor < source.Length && char.IsDigit(source[cursor]))
            {
                cursor++;
            }
            string decText = source[start..cursor];
            double decValue = double.Parse(decText, CultureInfo.InvariantCulture);
            i = cursor;
            token = new Token(TokenKind.Number, decText, start, decValue);
            error = null;
            return true;
        }

        if (cursor < source.Length && IsDurationUnitStart(source, cursor))
        {
            if (negative)
            {
                token = default;
                error = new QueryException("Negative durations are not supported.", start);
                // Advance past the malformed run so permissive mode doesn't loop.
                while (cursor < source.Length && (char.IsDigit(source[cursor]) || IsDurationUnitStart(source, cursor)))
                {
                    cursor++;
                }
                i = cursor;
                return false;
            }

            TimeSpan total = TimeSpan.Zero;
            int number = int.Parse(source[firstDigitStart..cursor], CultureInfo.InvariantCulture);
            total += ConsumeUnit(source, ref cursor, number);

            while (cursor < source.Length && char.IsDigit(source[cursor]))
            {
                int numStart = cursor;
                while (cursor < source.Length && char.IsDigit(source[cursor]))
                {
                    cursor++;
                }
                if (cursor >= source.Length || !IsDurationUnitStart(source, cursor))
                {
                    token = default;
                    error = new QueryException("Expected duration unit (h/m/s/ms).", cursor);
                    i = cursor;
                    return false;
                }
                int n = int.Parse(source[numStart..cursor], CultureInfo.InvariantCulture);
                total += ConsumeUnit(source, ref cursor, n);
            }

            string raw = source[start..cursor];
            i = cursor;
            token = new Token(TokenKind.Duration, raw, start, total);
            error = null;
            return true;
        }

        string numText = source[start..cursor];
        double value = double.Parse(numText, CultureInfo.InvariantCulture);
        i = cursor;
        token = new Token(TokenKind.Number, numText, start, value);
        error = null;
        return true;
    }

    private static bool IsDurationUnitStart(string source, int i)
    {
        char c = char.ToLowerInvariant(source[i]);
        return c == 'h' || c == 'm' || c == 's';
    }

    private static TimeSpan ConsumeUnit(string source, ref int cursor, int number)
    {
        char unit = char.ToLowerInvariant(source[cursor]);
        if (unit == 'm' && cursor + 1 < source.Length && char.ToLowerInvariant(source[cursor + 1]) == 's')
        {
            cursor += 2;
            return TimeSpan.FromMilliseconds(number);
        }
        cursor++;
        return unit switch
        {
            'h' => TimeSpan.FromHours(number),
            'm' => TimeSpan.FromMinutes(number),
            's' => TimeSpan.FromSeconds(number),
            _ => throw new QueryException($"Unknown duration unit '{unit}'.", cursor - 1),
        };
    }

    private static bool IsIdentStart(char c) => char.IsLetter(c) || c == '_';

    private static bool IsIdentPart(char c) => char.IsLetterOrDigit(c) || c == '_' || c == '.';

    // ───────────────────────── Parser ─────────────────────────

    private sealed class Parser
    {
        private readonly List<Token> _tokens;
        private int _pos;

        public Parser(List<Token> tokens)
        {
            _tokens = tokens;
        }

        private Token Peek => _tokens[_pos];
        private Token PeekAhead(int offset) => _pos + offset < _tokens.Count ? _tokens[_pos + offset] : _tokens[^1];

        private Token Consume()
        {
            var t = _tokens[_pos];
            _pos++;
            return t;
        }

        private Token Expect(TokenKind kind, string what)
        {
            if (Peek.Kind != kind)
            {
                throw new QueryException($"Expected {what} but found '{Peek.Text}'.", Peek.Position);
            }
            return Consume();
        }

        public void ExpectEof()
        {
            if (Peek.Kind != TokenKind.Eof)
            {
                throw new QueryException($"Unexpected trailing input '{Peek.Text}'.", Peek.Position);
            }
        }

        public QueryNode ParseExpression() => ParseOr();

        private QueryNode ParseOr()
        {
            var left = ParseAnd();
            while (Peek.Kind == TokenKind.Or)
            {
                Consume();
                var right = ParseAnd();
                left = new OrNode(left, right);
            }
            return left;
        }

        private QueryNode ParseAnd()
        {
            var left = ParseNot();
            while (Peek.Kind == TokenKind.And)
            {
                Consume();
                var right = ParseNot();
                left = new AndNode(left, right);
            }
            return left;
        }

        private QueryNode ParseNot()
        {
            if (Peek.Kind == TokenKind.Not)
            {
                Consume();
                return new NotNode(ParseNot());
            }
            return ParsePrimary();
        }

        private QueryNode ParsePrimary()
        {
            if (Peek.Kind == TokenKind.LParen)
            {
                Consume();
                var inner = ParseExpression();
                Expect(TokenKind.RParen, "')'");
                return inner;
            }
            return ParsePredicate();
        }

        private QueryNode ParsePredicate()
        {
            if (Peek.Kind != TokenKind.Identifier)
            {
                throw new QueryException($"Expected column name but found '{Peek.Text}'.", Peek.Position);
            }
            var column = Consume().Text;

            // Detect `NOT LIKE`, `NOT CONTAINS`, `NOT STARTSWITH`, `NOT ENDSWITH`,
            // `NOT IN`, `NOT BETWEEN` as negated predicates.
            bool negated = false;
            if (Peek.Kind == TokenKind.Not)
            {
                var nextKind = PeekAhead(1).Kind;
                if (nextKind is TokenKind.Like or TokenKind.Contains or TokenKind.StartsWith
                    or TokenKind.EndsWith or TokenKind.In or TokenKind.Between)
                {
                    negated = true;
                    Consume();
                }
            }

            switch (Peek.Kind)
            {
                case TokenKind.Eq:
                case TokenKind.Neq:
                case TokenKind.Lt:
                case TokenKind.Lte:
                case TokenKind.Gt:
                case TokenKind.Gte:
                {
                    if (negated)
                    {
                        throw new QueryException("Unexpected NOT before comparison.", Peek.Position);
                    }
                    var opTok = Consume();
                    var value = ParseValue();
                    return new ComparisonNode(column, MapOp(opTok.Kind), value);
                }
                case TokenKind.Like:
                {
                    Consume();
                    var pattern = ExpectStringValue("string pattern");
                    return new LikeNode(column, pattern, negated);
                }
                case TokenKind.Contains:
                {
                    Consume();
                    var text = ExpectStringValue("string");
                    return new StringMatchNode(column, text, StringMatchKind.Contains, negated);
                }
                case TokenKind.StartsWith:
                {
                    Consume();
                    var text = ExpectStringValue("string");
                    return new StringMatchNode(column, text, StringMatchKind.StartsWith, negated);
                }
                case TokenKind.EndsWith:
                {
                    Consume();
                    var text = ExpectStringValue("string");
                    return new StringMatchNode(column, text, StringMatchKind.EndsWith, negated);
                }
                case TokenKind.In:
                {
                    Consume();
                    Expect(TokenKind.LParen, "'('");
                    var values = new List<ValueNode>();
                    values.Add(ParseValue());
                    while (Peek.Kind == TokenKind.Comma)
                    {
                        Consume();
                        values.Add(ParseValue());
                    }
                    Expect(TokenKind.RParen, "')'");
                    return new InNode(column, values, negated);
                }
                case TokenKind.Between:
                {
                    Consume();
                    var low = ParseValue();
                    Expect(TokenKind.And, "AND");
                    var high = ParseValue();
                    return new BetweenNode(column, low, high, negated);
                }
                case TokenKind.Is:
                {
                    if (negated)
                    {
                        throw new QueryException("Use `IS NOT NULL` instead of `NOT IS NULL`.", Peek.Position);
                    }
                    Consume();
                    bool isNotNull = false;
                    if (Peek.Kind == TokenKind.Not)
                    {
                        Consume();
                        isNotNull = true;
                    }
                    Expect(TokenKind.Null, "NULL");
                    return new IsNullNode(column, Negated: isNotNull);
                }
                default:
                    throw new QueryException($"Expected comparison operator, LIKE, IN, BETWEEN, or IS after column '{column}'.", Peek.Position);
            }
        }

        private static ComparisonOp MapOp(TokenKind kind) => kind switch
        {
            TokenKind.Eq => ComparisonOp.Eq,
            TokenKind.Neq => ComparisonOp.Neq,
            TokenKind.Lt => ComparisonOp.Lt,
            TokenKind.Lte => ComparisonOp.Lte,
            TokenKind.Gt => ComparisonOp.Gt,
            TokenKind.Gte => ComparisonOp.Gte,
            _ => throw new InvalidOperationException($"Not a comparison: {kind}"),
        };

        private ValueNode ParseValue()
        {
            var tok = Peek;
            switch (tok.Kind)
            {
                case TokenKind.String:
                    Consume();
                    return new StringValue(tok.Text);
                case TokenKind.Number:
                    Consume();
                    return new NumberValue((double)tok.Payload!, tok.Text);
                case TokenKind.Duration:
                    Consume();
                    return new DurationValue((TimeSpan)tok.Payload!, tok.Text);
                case TokenKind.True:
                    Consume();
                    return new BoolValue(true);
                case TokenKind.False:
                    Consume();
                    return new BoolValue(false);
                case TokenKind.Null:
                    Consume();
                    return new NullValue();
                case TokenKind.Identifier:
                    // Function call: NOW() or TODAY(). The only identifier allowed in
                    // a value position is a zero-arg function.
                    return ParseFunctionCall();
                default:
                    throw new QueryException($"Expected a value but found '{tok.Text}'.", tok.Position);
            }
        }

        private ValueNode ParseFunctionCall()
        {
            var nameTok = Consume();
            Expect(TokenKind.LParen, "'('");
            Expect(TokenKind.RParen, "')'");
            if (string.Equals(nameTok.Text, "NOW", StringComparison.OrdinalIgnoreCase))
            {
                return new DateTimeValue(DateTime.Now);
            }
            if (string.Equals(nameTok.Text, "TODAY", StringComparison.OrdinalIgnoreCase))
            {
                return new DateTimeValue(DateTime.Today);
            }
            throw new QueryException($"Unknown function '{nameTok.Text}'. Supported: NOW(), TODAY().", nameTok.Position);
        }

        private string ExpectStringValue(string what)
        {
            if (Peek.Kind != TokenKind.String)
            {
                throw new QueryException($"Expected {what} but found '{Peek.Text}'.", Peek.Position);
            }
            return Consume().Text;
        }
    }
}
