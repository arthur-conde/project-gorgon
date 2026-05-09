namespace Gandalf.Domain;

/// <summary>
/// Pure decision logic for "did this timer finish while the user was playing another
/// character?" Separated from <c>TimerListViewModel</c> so it's unit-testable without
/// the VM's WPF/Dispatcher entanglements.
/// </summary>
public static class ElapsedWhileAwayClassifier
{
    /// <summary>
    /// True iff the timer was running when the user last left this character
    /// (<paramref name="lastActiveAt"/>) AND would have finished before <paramref name="now"/>.
    /// Returns false when <paramref name="lastActiveAt"/> is null (first-ever session — don't
    /// flag everything as catch-up noise).
    /// </summary>
    /// <remarks>
    /// Uses the row's pre-computed firing instant (<paramref name="firingAt"/>),
    /// not <c>CompletedAt</c>. <c>CompletedAt</c> is stamped by the tick loop
    /// which may not have run on the newly-active character yet — the firing
    /// instant is tick-order-independent. Game-clock alarms have a firing
    /// instant that isn't <c>StartedAt + Duration</c>, which is why this took
    /// a <c>TimeSpan duration</c> parameter historically and now takes the
    /// instant directly.
    /// </remarks>
    public static bool IsElapsedWhileAway(
        TimerProgress progress,
        DateTimeOffset firingAt,
        DateTimeOffset? lastActiveAt,
        DateTimeOffset now)
    {
        if (lastActiveAt is null) return false;
        if (progress.StartedAt is null) return false;

        var started = progress.StartedAt.Value;
        return started <= lastActiveAt.Value
            && firingAt > lastActiveAt.Value
            && firingAt <= now;
    }
}
