using System.Globalization;

namespace Arda.Dispatch;

/// <summary>
/// Stack-only positional tokenizer for Process* verb arguments.
/// Advances through comma-delimited positional args on a span without allocation.
/// <para>
/// Typical usage: construct with the args span (starting at '('), call
/// <see cref="SkipOpen"/>, then call <c>Next*</c> methods in positional order.
/// </para>
/// <para>
/// <c>Next*</c> methods throw <see cref="GrammarException"/> on malformed
/// input — the default for required fields. Use the <c>TryNext*</c> siblings
/// only for fields that are legitimately optional (e.g. trailing args added
/// in newer game versions, unknown enum tokens).
/// </para>
/// </summary>
public ref struct ArgTokenizer
{
    private ReadOnlySpan<char> _remaining;
    private readonly string _verb;
    private readonly string _sourceLog;

    /// <summary>
    /// Construct a tokenizer with verb + source-line context for throw
    /// reporting. The verb is allocated to a string at construction (cold
    /// path is the throw path; this keeps the throw site simple).
    /// </summary>
    public ArgTokenizer(ReadOnlySpan<char> args, ReadOnlySpan<char> verb, string sourceLog)
    {
        _remaining = args;
        _verb = verb.ToString();
        _sourceLog = sourceLog;
    }

    /// <summary>Skip past the opening '(' if present.</summary>
    public void SkipOpen()
    {
        if (_remaining.Length > 0 && _remaining[0] == '(')
            _remaining = _remaining[1..];
    }

    /// <summary>Read next token as <see cref="long"/>. Throws <see cref="GrammarException"/> on parse failure.</summary>
    public long NextLong()
    {
        var token = NextRawToken();
        if (!long.TryParse(token, NumberStyles.Integer, CultureInfo.InvariantCulture, out var value))
            throw GrammarFail(token, "expected long");
        return value;
    }

    /// <summary>Read next token as <see cref="int"/>. Throws <see cref="GrammarException"/> on parse failure.</summary>
    public int NextInt()
    {
        var token = NextRawToken();
        if (!int.TryParse(token, NumberStyles.Integer, CultureInfo.InvariantCulture, out var value))
            throw GrammarFail(token, "expected int");
        return value;
    }

    /// <summary>Read next token as <see cref="double"/>. Throws <see cref="GrammarException"/> on parse failure.</summary>
    public double NextDouble()
    {
        var token = NextRawToken();
        if (!double.TryParse(token, NumberStyles.Float, CultureInfo.InvariantCulture, out var value))
            throw GrammarFail(token, "expected double");
        return value;
    }

    /// <summary>Read next token as <see cref="bool"/>.</summary>
    public bool NextBool()
    {
        var token = NextRawToken();
        return token is "True" or "true" or "1";
    }

    /// <summary>
    /// Try-variant of <see cref="NextLong"/>. Returns <c>false</c> without
    /// throwing if no token remains or the token cannot be parsed. Reserved
    /// for legitimately-optional fields.
    /// </summary>
    public bool TryNextLong(out long value)
    {
        if (!TryPeekRawToken(out var token) ||
            !long.TryParse(token, NumberStyles.Integer, CultureInfo.InvariantCulture, out value))
        {
            value = 0;
            return false;
        }

        ConsumeRawToken();
        return true;
    }

    /// <summary>Try-variant of <see cref="NextInt"/>. See <see cref="TryNextLong"/>.</summary>
    public bool TryNextInt(out int value)
    {
        if (!TryPeekRawToken(out var token) ||
            !int.TryParse(token, NumberStyles.Integer, CultureInfo.InvariantCulture, out value))
        {
            value = 0;
            return false;
        }

        ConsumeRawToken();
        return true;
    }

    /// <summary>Try-variant of <see cref="NextDouble"/>. See <see cref="TryNextLong"/>.</summary>
    public bool TryNextDouble(out double value)
    {
        if (!TryPeekRawToken(out var token) ||
            !double.TryParse(token, NumberStyles.Float, CultureInfo.InvariantCulture, out value))
        {
            value = 0;
            return false;
        }

        ConsumeRawToken();
        return true;
    }

    /// <summary>Try-variant of <see cref="NextBool"/>. Always consumes the token if present.</summary>
    public bool TryNextBool(out bool value)
    {
        if (!HasMore)
        {
            value = false;
            return false;
        }

        value = NextBool();
        return true;
    }

    /// <summary>
    /// Read next quoted string as a span (no allocation).
    /// The returned span excludes the surrounding quotes.
    /// </summary>
    public ReadOnlySpan<char> NextQuotedSpan()
    {
        SkipWhitespace();

        if (_remaining.Length == 0 || _remaining[0] != '"')
            return NextRawToken();

        _remaining = _remaining[1..];
        var closeIdx = _remaining.IndexOf('"');
        if (closeIdx < 0)
        {
            var all = _remaining;
            _remaining = ReadOnlySpan<char>.Empty;
            return all;
        }

        var value = _remaining[..closeIdx];
        _remaining = _remaining[(closeIdx + 1)..];
        SkipDelimiter();
        return value;
    }

    /// <summary>Read next unquoted token as a span (no allocation).</summary>
    public ReadOnlySpan<char> NextTokenSpan() => NextRawToken();

    /// <summary>
    /// Try-variant of <see cref="NextTokenSpan"/>. Returns <c>false</c> when
    /// no more tokens remain.
    /// </summary>
    public bool TryNextTokenSpan(out ReadOnlySpan<char> token)
    {
        if (!HasMore)
        {
            token = ReadOnlySpan<char>.Empty;
            return false;
        }

        token = NextRawToken();
        return true;
    }

    /// <summary>
    /// Try-variant of <see cref="NextQuotedSpan"/>. Returns <c>false</c> when
    /// no more tokens remain.
    /// </summary>
    public bool TryNextQuotedSpan(out ReadOnlySpan<char> value)
    {
        if (!HasMore)
        {
            value = ReadOnlySpan<char>.Empty;
            return false;
        }

        value = NextQuotedSpan();
        return true;
    }

    /// <summary>Read a bracketed array [...] as a span (no allocation).</summary>
    public ReadOnlySpan<char> NextBracketedSpan() => NextBalanced('[', ']');

    /// <summary>Read a braced struct {...} as a span (no allocation).</summary>
    public ReadOnlySpan<char> NextBracedSpan() => NextBalanced('{', '}');

    /// <summary>Skip <paramref name="count"/> positional args without parsing.</summary>
    public void Skip(int count)
    {
        for (var i = 0; i < count; i++)
        {
            SkipWhitespace();
            if (_remaining.Length == 0) return;

            if (_remaining[0] == '"')
            {
                NextQuotedSpan();
            }
            else if (_remaining[0] == '[')
            {
                NextBracketedSpan();
            }
            else if (_remaining[0] == '{')
            {
                NextBracedSpan();
            }
            else
            {
                NextRawToken();
            }
        }
    }

    /// <summary>Returns <c>true</c> if there are remaining characters to parse.</summary>
    public readonly bool HasMore => _remaining.Length > 0 && _remaining[0] != ')';

    private ReadOnlySpan<char> NextRawToken()
    {
        SkipWhitespace();

        var end = _remaining.IndexOfAny(',', ')');
        if (end < 0)
        {
            var all = _remaining;
            _remaining = ReadOnlySpan<char>.Empty;
            return all.TrimEnd();
        }

        var token = _remaining[..end].TrimEnd();
        _remaining = _remaining[end..];
        SkipDelimiter();
        return token;
    }

    private bool TryPeekRawToken(out ReadOnlySpan<char> token)
    {
        SkipWhitespace();
        if (_remaining.Length == 0 || _remaining[0] == ')')
        {
            token = ReadOnlySpan<char>.Empty;
            return false;
        }

        var end = _remaining.IndexOfAny(',', ')');
        token = end < 0 ? _remaining.TrimEnd() : _remaining[..end].TrimEnd();
        return true;
    }

    private void ConsumeRawToken()
    {
        var end = _remaining.IndexOfAny(',', ')');
        if (end < 0)
        {
            _remaining = ReadOnlySpan<char>.Empty;
            return;
        }
        _remaining = _remaining[end..];
        SkipDelimiter();
    }

    private ReadOnlySpan<char> NextBalanced(char open, char close)
    {
        SkipWhitespace();
        if (_remaining.Length == 0 || _remaining[0] != open)
            return NextRawToken();

        var depth = 0;
        for (var i = 0; i < _remaining.Length; i++)
        {
            if (_remaining[i] == open) depth++;
            else if (_remaining[i] == close) depth--;

            if (depth == 0)
            {
                var value = _remaining[1..i];
                _remaining = _remaining[(i + 1)..];
                SkipDelimiter();
                return value;
            }
        }

        var all = _remaining[1..];
        _remaining = ReadOnlySpan<char>.Empty;
        return all;
    }

    private void SkipWhitespace()
    {
        _remaining = _remaining.TrimStart(' ');
    }

    private void SkipDelimiter()
    {
        if (_remaining.Length > 0 && _remaining[0] == ',')
        {
            _remaining = _remaining[1..];
            SkipWhitespace();
        }
    }

    private GrammarException GrammarFail(ReadOnlySpan<char> token, string hint) =>
        new(_verb, _sourceLog, token.ToString(), hint);
}
