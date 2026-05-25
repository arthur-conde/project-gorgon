namespace Arda.Dispatch;

/// <summary>
/// Shared span-based helpers for handler argument parsing.
/// </summary>
public static class SpanHelpers
{
    /// <summary>
    /// Strip enclosing parentheses from an args span. Handles both fully-enclosed
    /// <c>(content)</c> and partially-enclosed <c>(content</c> forms.
    /// </summary>
    public static ReadOnlySpan<char> StripParens(ReadOnlySpan<char> span)
    {
        if (span.Length >= 2 && span[0] == '(' && span[^1] == ')')
            return span[1..^1];
        if (span.Length >= 1 && span[0] == '(')
            return span[1..];
        return span;
    }
}
