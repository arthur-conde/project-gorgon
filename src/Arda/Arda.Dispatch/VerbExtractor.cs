namespace Arda.Dispatch;

/// <summary>
/// Extracts the verb span from a log line's content (timestamp prefix already stripped).
/// Returns a <see cref="ReadOnlySpan{T}"/> into the source — zero allocation.
/// </summary>
internal static class VerbExtractor
{
    private const string LocalPlayerPrefix = "LocalPlayer: ";

    /// <summary>
    /// Extract the verb key from a log line span. The returned span is a slice
    /// of the input — no string allocation occurs.
    /// </summary>
    /// <returns>
    /// The verb span (e.g. "ProcessDeleteItem"), or empty if the line has no
    /// recognizable verb structure.
    /// </returns>
    public static ReadOnlySpan<char> Extract(ReadOnlySpan<char> log)
    {
        if (log.StartsWith(LocalPlayerPrefix))
        {
            var afterPrefix = log[LocalPlayerPrefix.Length..];
            var parenIdx = afterPrefix.IndexOf('(');
            return parenIdx > 0 ? afterPrefix[..parenIdx] : afterPrefix;
        }

        if (log.StartsWith("LOADING LEVEL"))
            return "LOADING_LEVEL";

        if (log.StartsWith("!!! Initializing area!"))
            return "InitializingArea";

        return ReadOnlySpan<char>.Empty;
    }

    /// <summary>
    /// Extract the args portion from a log line span, given the verb has already
    /// been identified. Returns the span starting at the opening '(' (inclusive)
    /// for Process* verbs, or the content after the verb discriminator for system lines.
    /// </summary>
    public static ReadOnlySpan<char> ExtractArgs(ReadOnlySpan<char> log)
    {
        if (log.StartsWith(LocalPlayerPrefix))
        {
            var afterPrefix = log[LocalPlayerPrefix.Length..];
            var parenIdx = afterPrefix.IndexOf('(');
            return parenIdx > 0 ? afterPrefix[parenIdx..] : ReadOnlySpan<char>.Empty;
        }

        if (log.StartsWith("LOADING LEVEL "))
            return log["LOADING LEVEL ".Length..];

        if (log.StartsWith("!!! Initializing area! "))
            return log["!!! Initializing area! ".Length..];

        return ReadOnlySpan<char>.Empty;
    }
}
