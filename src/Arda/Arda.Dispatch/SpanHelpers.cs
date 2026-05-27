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

    /// <summary>
    /// Produce a zero-copy <see cref="ReadOnlyMemory{T}"/> slice from the source log string.
    /// If the span overlaps the source (the common case for ArgTokenizer output), returns
    /// a memory slice with no allocation. Falls back to <c>ToString().AsMemory()</c> for
    /// spans that don't overlap (e.g. stack-constructed sub-slices).
    /// </summary>
    public static ReadOnlyMemory<char> SliceFromSource(string sourceLog, ReadOnlySpan<char> span)
    {
        if (span.IsEmpty) return ReadOnlyMemory<char>.Empty;
        var sourceSpan = sourceLog.AsSpan();
        if (sourceSpan.Overlaps(span, out var offset))
            return sourceLog.AsMemory(offset, span.Length);
        return span.ToString().AsMemory();
    }
}
