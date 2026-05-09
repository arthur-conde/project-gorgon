namespace Gandalf.Domain;

/// <summary>
/// Join of a timer's <see cref="GandalfTimerDef"/> (what the timer is) with the active
/// character's <see cref="TimerProgress"/> (what it's doing right now). The UI binds to
/// this record — it carries the computed state/remaining/fraction that previously lived
/// on <c>GandalfTimer</c>.
/// </summary>
public sealed record TimerView(GandalfTimerDef Def, TimerProgress Progress)
{
    /// <summary>
    /// The instant this Running timer fires. Falls back to
    /// <c>StartedAt + Def.Duration</c> when <see cref="TimerProgress.FiringAt"/>
    /// hasn't been stamped yet — keeps countdown semantics unchanged for any
    /// caller that constructs a <c>TimerView</c> outside the
    /// <c>TimerProgressService</c> lifecycle (e.g. tests).
    /// </summary>
    private DateTimeOffset? FiringAt =>
        Progress.StartedAt is null
            ? null
            : Progress.FiringAt ?? Progress.StartedAt.Value + Def.Duration;

    public TimerState State
    {
        get
        {
            if (Progress.StartedAt is null) return TimerState.Idle;
            if (Progress.CompletedAt is not null) return TimerState.Done;
            if (FiringAt is { } at && DateTimeOffset.UtcNow >= at) return TimerState.Done;
            return TimerState.Running;
        }
    }

    public TimeSpan Remaining
    {
        get
        {
            if (FiringAt is not { } at) return Def.Duration;
            var left = at - DateTimeOffset.UtcNow;
            return left < TimeSpan.Zero ? TimeSpan.Zero : left;
        }
    }

    public double Fraction
    {
        get
        {
            if (Progress.StartedAt is null || FiringAt is not { } at) return 0.0;
            var total = at - Progress.StartedAt.Value;
            if (total <= TimeSpan.Zero) return 1.0;
            return Math.Clamp((DateTimeOffset.UtcNow - Progress.StartedAt.Value) / total, 0.0, 1.0);
        }
    }

    public string GroupKey => Def.GroupKey;
}
