namespace Arda.Dispatch;

/// <summary>
/// Thrown when <see cref="ArgTokenizer"/> encounters a token it cannot parse
/// against the expected grammar. Propagates out of <see cref="DispatchTable"/>
/// so <c>WorldDriver</c> can halt the simulation — this is not a recoverable
/// per-handler failure.
/// </summary>
public sealed class GrammarException : Exception
{
    private const int MaxExcerptLength = 256;

    public string Verb { get; }
    public string SourceLine { get; }
    public string TokenExcerpt { get; }
    public string ParserHint { get; }

    public GrammarException(
        string verb,
        string sourceLine,
        string tokenExcerpt,
        string parserHint,
        Exception? inner = null)
        : base($"Grammar drift on verb {verb}: {parserHint} (token '{Truncate(tokenExcerpt, 64)}')", inner)
    {
        Verb = verb;
        SourceLine = Truncate(sourceLine, MaxExcerptLength);
        TokenExcerpt = Truncate(tokenExcerpt, MaxExcerptLength);
        ParserHint = parserHint;
    }

    private static string Truncate(string value, int max) =>
        value.Length <= max ? value : value[..max] + "…";
}
