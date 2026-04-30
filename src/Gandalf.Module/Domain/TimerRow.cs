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

    public TimerState State
    {
        get
        {
            if (Progress is null) return TimerState.Idle;
            if (Progress.DismissedAt is not null) return TimerState.Idle;
            if (Clock.GetUtcNow() - Progress.StartedAt >= Catalog.Duration) return TimerState.Done;
            return TimerState.Running;
        }
    }

    public TimeSpan Remaining
    {
        get
        {
            if (Progress is null) return Catalog.Duration;
            var left = Catalog.Duration - (Clock.GetUtcNow() - Progress.StartedAt);
            return left < TimeSpan.Zero ? TimeSpan.Zero : left;
        }
    }

    public DateTimeOffset? CompletedAt
    {
        get
        {
            if (Progress is null) return null;
            var stamped = Progress.StartedAt + Catalog.Duration;
            return Clock.GetUtcNow() >= stamped ? stamped : null;
        }
    }

    public double Fraction
    {
        get
        {
            if (Progress is null) return 0.0;
            if (Catalog.Duration <= TimeSpan.Zero) return 1.0;
            return Math.Clamp((Clock.GetUtcNow() - Progress.StartedAt) / Catalog.Duration, 0.0, 1.0);
        }
    }
}
