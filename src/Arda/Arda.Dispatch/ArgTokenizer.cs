namespace Arda.Dispatch;

/// <summary>
/// Stack-only positional tokenizer for Process* verb arguments.
/// Advances through comma-delimited positional args on a span without allocation.
/// <para>
/// Typical usage: construct with the args span (starting at '('), call
/// <see cref="SkipOpen"/>, then call <c>Next*</c> methods in positional order.
/// </para>
/// </summary>
public ref struct ArgTokenizer
{
    private ReadOnlySpan<char> _remaining;

    public ArgTokenizer(ReadOnlySpan<char> args) => _remaining = args;

    /// <summary>Skip past the opening '(' if present.</summary>
    public void SkipOpen()
    {
        if (_remaining.Length > 0 && _remaining[0] == '(')
            _remaining = _remaining[1..];
    }

    /// <summary>Read next token as <see cref="long"/>. Advances past trailing delimiter.</summary>
    public long NextLong()
    {
        var token = NextRawToken();
        return long.Parse(token);
    }

    /// <summary>Read next token as <see cref="int"/>. Advances past trailing delimiter.</summary>
    public int NextInt()
    {
        var token = NextRawToken();
        return int.Parse(token);
    }

    /// <summary>Read next token as <see cref="double"/>. Advances past trailing delimiter.</summary>
    public double NextDouble()
    {
        var token = NextRawToken();
        return double.Parse(token, System.Globalization.CultureInfo.InvariantCulture);
    }

    /// <summary>Read next token as <see cref="bool"/>. Advances past trailing delimiter.</summary>
    public bool NextBool()
    {
        var token = NextRawToken();
        return token is "True" or "true" or "1";
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

        _remaining = _remaining[1..]; // skip opening quote
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
                var value = _remaining[1..i]; // exclude outer brackets
                _remaining = _remaining[(i + 1)..];
                SkipDelimiter();
                return value;
            }
        }

        var all = _remaining[1..]; // unclosed — return everything after open
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
}
