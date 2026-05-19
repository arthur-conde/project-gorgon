namespace Mithril.Shared.Logging;

/// <summary>
/// Per-source timestamp grammar. L0 of the layered log pipeline (#511 /
/// #513) is parameterized by one of these so the duplicated Player vs
/// chat tail+stamp logic collapses onto a single <c>LogSourceTailer</c>:
/// the tailer owns offset/residual/rotation/Sequence/ReadMonotonicTicks,
/// the clock owns "interpret this raw line as an absolute
/// <see cref="DateTimeOffset"/>." Implementations live alongside the
/// tailer in <c>Mithril.Shared.Logging</c>:
///
/// <list type="bullet">
///   <item><see cref="PlayerLogClock"/> — Player.log <c>[HH:MM:SS]</c>
///   prefix, UTC, no date; folds time-of-day over a session-anchor /
///   mtime date with midnight-rollover detection. Emits offset
///   <see cref="TimeSpan.Zero"/>.</item>
///   <item><see cref="ChatLogClock"/> — chat <c>yy-MM-dd HH:mm:ss\t…</c>
///   prefix, LOCAL, date in line; emits the host's local UTC offset for
///   that instant. This is what closes the #183 bug class on the chat
///   side: a downstream consumer that copies the Player-side
///   <c>TimeSpan.Zero</c> idiom would be wrong by the local offset, but
///   downstream of L0 there is no per-consumer conversion left to get
///   wrong.</item>
/// </list>
/// </summary>
internal interface ILogSourceClock
{
    /// <summary>
    /// Compute the absolute instant for a single tailed line. Lines
    /// without a recognisable prefix inherit the prior stamp (engine
    /// noise interleaved with gameplay lines stays anchored on the most
    /// recent recognised line); a leading run of unprefixed lines falls
    /// through to <see cref="TimeProvider.GetUtcNow"/>.
    /// </summary>
    DateTimeOffset StampForLine(string line);

    /// <summary>
    /// One-shot pre-stamp hook for sources whose date isn't in-line.
    /// <see cref="PlayerLogClock"/> uses this to anchor the UTC date for
    /// the first content batch (session anchor preferred, mtime
    /// fallback). <see cref="ChatLogClock"/> no-ops — the date is on
    /// every line so no anchoring is needed. Called once per source by
    /// <c>LogSourceTailer</c> on the first non-empty batch.
    /// </summary>
    void EnsureAnchored(IReadOnlyList<string> lines, Func<DateTime> mtimeUtcAccessor);
}
