namespace Arda.Contracts.State.Health;

/// <summary>
/// Captured details of a grammar drift that halted an Arda driver. Surfaced
/// via <see cref="IWorldHealthView.Break"/> for the shell banner.
/// </summary>
public sealed record GrammarBreak(
    string SourceFamily,
    string Verb,
    string SourceLine,
    string TokenExcerpt,
    string ParserHint,
    DateTimeOffset At);
