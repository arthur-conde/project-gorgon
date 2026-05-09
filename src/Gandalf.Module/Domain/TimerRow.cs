namespace Gandalf.Domain;

/// <summary>
/// Generic join of one <see cref="TimerCatalogEntry"/> with the matching
/// <see cref="TimerProgressEntry"/> (or null if the row has never been started).
/// The cross-source render path consumes this — the User-feed-specific
/// <see cref="TimerView"/> remains in use inside <c>UserTimerSource</c> and
/// <c>TimerProgressService</c> for User-only state-machine checks.
/// </summary>
public sealed record TimerRow(TimerCatalogEntry Catalog, TimerProgressEntry? Progress)
{
    /// <summary>
    /// Clock used to compute <see cref="State"/>, <see cref="Remaining"/>,
    /// <see cref="CompletedAt"/>, and <see cref="Fraction"/>. Defaults to wall
    /// clock; tests override with a controlled <see cref="TimeProvider"/> so
    /// state assertions don't drift with real time.
    /// </summary>
    public TimeProvider Clock { get; init; } = TimeProvider.System;

    public string Key => Catalog.Key;
    public string Name => Catalog.DisplayName;
    public string? Region => Catalog.Region;
    public TimeSpan Duration => Catalog.Duration;
    public string GroupKey => Catalog.Region ?? "";

    /// <summary>
    /// Wall-clock instant this Running timer fires. For sources that stamp a
    /// per-row firing instant (User feed's game-clock alarms), this comes from
    /// <see cref="TimerProgressEntry.FiringAt"/>. Falls back to
    /// <c>StartedAt + Duration</c> for derived sources (Quest/Loot) whose firing
    /// moment is always exactly that — they leave the field null and inherit
    /// today's behavior verbatim.
    /// </summary>
    public DateTimeOffset? FiringAt =>
        Progress is null
            ? null
            : Progress.FiringAt ?? Progress.StartedAt + Catalog.Duration;

    public TimerState State
    {
        get
        {
            if (Progress is null) return TimerState.Idle;
            if (Progress.DismissedAt is not null) return TimerState.Idle;
            if (FiringAt is { } at && Clock.GetUtcNow() >= at) return TimerState.Done;
            return TimerState.Running;
        }
    }

    public TimeSpan Remaining
    {
        get
        {
            if (FiringAt is not { } at) return Catalog.Duration;
            var left = at - Clock.GetUtcNow();
            return left < TimeSpan.Zero ? TimeSpan.Zero : left;
        }
    }

    public DateTimeOffset? CompletedAt
    {
        get
        {
            if (FiringAt is not { } at) return null;
            return Clock.GetUtcNow() >= at ? at : null;
        }
    }

    public double Fraction
    {
        get
        {
            if (Progress is null || FiringAt is not { } at) return 0.0;
            var total = at - Progress.StartedAt;
            if (total <= TimeSpan.Zero) return 1.0;
            return Math.Clamp((Clock.GetUtcNow() - Progress.StartedAt) / total, 0.0, 1.0);
        }
    }

    /// <summary>
    /// Wall-clock instant at which any visible property of this row would next
    /// flip. <c>null</c> for rows that don't change by time alone (Idle, or a
    /// dismissed row with no upcoming resurrection). The display scheduler
    /// orders rows by this value and only refreshes those whose moment has
    /// arrived, replacing the per-tab 1 Hz tick that previously refreshed every
    /// row regardless of whether anything changed.
    /// <list type="bullet">
    /// <item><b>Running:</b> the soonest of the state-flip moment
    /// (<c>StartedAt + Duration</c>) and the next minute boundary — <c>"Xh Ym
    /// remaining"</c> only changes when minutes roll.</item>
    /// <item><b>Just-Done</b> (&lt; 60 s since completion): when <c>"done!"</c>
    /// flips to <c>"done 1m ago"</c> at <c>CompletedAt + 60 s</c>.</item>
    /// <item><b>Old Done:</b> the next minute boundary, when <c>"done Xm ago"</c>
    /// rolls.</item>
    /// </list>
    /// <see cref="Fraction"/> is intentionally <em>not</em> driven by this
    /// property — sub-minute progress-bar smoothness uses a separate gated
    /// 1 Hz fast-tick that runs only while at least one Running row is
    /// visible.
    /// </summary>
    public DateTimeOffset? NextDisplayChangeAt
    {
        get
        {
            if (Progress is null) return null;
            if (Progress.DismissedAt is not null) return null;

            var now = Clock.GetUtcNow();
            if (FiringAt is not { } stateFlipAt) return null;

            if (now < stateFlipAt)
            {
                var nextMinute = NextMinuteBoundaryAfter(now);
                return stateFlipAt < nextMinute ? stateFlipAt : nextMinute;
            }

            var elapsedSinceDone = now - stateFlipAt;
            return elapsedSinceDone < TimeSpan.FromSeconds(60)
                ? stateFlipAt + TimeSpan.FromSeconds(60)
                : NextMinuteBoundaryAfter(now);
        }
    }

    private static DateTimeOffset NextMinuteBoundaryAfter(DateTimeOffset t)
    {
        var floor = new DateTimeOffset(t.Year, t.Month, t.Day, t.Hour, t.Minute, 0, t.Offset);
        return floor + TimeSpan.FromMinutes(1);
    }
}
