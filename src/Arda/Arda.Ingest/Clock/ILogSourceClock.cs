namespace Arda.Ingest.Clock;

/// <summary>
/// Per-source timestamp grammar for the L0 layer. The clock knows how to
/// extract an absolute <see cref="DateTimeOffset"/> from a raw line span
/// and reports how many characters it consumed as the timestamp prefix.
/// <para>
/// Two implementations exist:
/// <list type="bullet">
///   <item><see cref="PlayerLogClock"/> — Player.log <c>[HH:MM:SS] </c>
///   prefix (UTC, no date). Folds time-of-day over a session-anchor or
///   mtime-derived date with midnight-rollover detection. Reports 11
///   characters consumed.</item>
///   <item><see cref="ChatLogClock"/> — Chat log <c>yy-MM-dd HH:mm:ss\t</c>
///   prefix (local time, date in line). Reports 18 characters consumed.</item>
/// </list>
/// </para>
/// <para>
/// The classifier (L1) uses <see cref="ClockResult.ConsumedLength"/> to
/// slice past the prefix without re-parsing — single source of truth for
/// "what's a timestamp prefix" lives here.
/// </para>
/// </summary>
internal interface ILogSourceClock
{
    /// <summary>
    /// Attempt to extract a timestamp from the line span. Returns
    /// <see cref="ClockResult.None"/> if the line has no recognizable
    /// prefix (engine noise, continuation lines). Lines without a prefix
    /// inherit the most recent successful timestamp from prior lines.
    /// </summary>
    ClockResult TryParse(ReadOnlySpan<char> line);

    /// <summary>
    /// One-shot anchoring hook for sources whose date isn't inline.
    /// <see cref="PlayerLogClock"/> uses this to derive the UTC date for
    /// the first batch (from session anchor or file mtime fallback).
    /// <see cref="ChatLogClock"/> no-ops — the date is on every line.
    /// Called once per source by the coordinator on the first non-empty batch.
    /// </summary>
    /// <param name="firstBatchLines">All lines in the first batch (for
    /// midnight-rollover counting in the Player clock).</param>
    /// <param name="mtimeUtcAccessor">Lazy accessor for the source file's
    /// last-write-time UTC (avoids the stat call if the session anchor
    /// is available).</param>
    void EnsureAnchored(
        ReadOnlySpan<(int Start, int Length)> firstBatchLines,
        char[] buffer,
        Func<DateTime> mtimeUtcAccessor);
}

/// <summary>
/// The result of an <see cref="ILogSourceClock.TryParse"/> call.
/// </summary>
internal readonly record struct ClockResult(
    /// <summary>
    /// The parsed absolute timestamp for this line. <c>null</c> if the
    /// line has no recognizable prefix.
    /// </summary>
    DateTimeOffset? Timestamp,

    /// <summary>
    /// Number of characters consumed as the timestamp prefix (including
    /// any trailing space or tab). Zero if no prefix was recognized.
    /// The classifier slices <c>line[ConsumedLength..]</c> to produce the
    /// stripped game-event text.
    /// </summary>
    int ConsumedLength)
{
    /// <summary>Sentinel for lines with no recognized timestamp prefix.</summary>
    public static ClockResult None => default;

    /// <summary>Whether a timestamp was successfully parsed.</summary>
    public bool HasTimestamp => Timestamp.HasValue;
}
