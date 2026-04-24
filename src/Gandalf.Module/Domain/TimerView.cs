namespace Gandalf.Domain;

/// <summary>
/// Join of a timer's <see cref="GandalfTimerDef"/> (what the timer is) with the active
/// character's <see cref="TimerProgress"/> (what it's doing right now). The UI binds to
/// this record — it carries the computed state/remaining/fraction that previously lived
/// on <c>GandalfTimer</c>.
/// </summary>
public sealed record TimerView(GandalfTimerDef Def, TimerProgress Progress)
{
    public TimerState State
    {
        get
        {
            if (Progress.StartedAt is null) return TimerState.Idle;
            if (Progress.CompletedAt is not null) return TimerState.Done;
            if (DateTimeOffset.UtcNow - Progress.StartedAt.Value >= Def.Duration) return TimerState.Done;
            return TimerState.Running;
        }
    }

    public TimeSpan Remaining
    {
        get
        {
            if (Progress.StartedAt is null) return Def.Duration;
            var left = Def.Duration - (DateTimeOffset.UtcNow - Progress.StartedAt.Value);
            return left < TimeSpan.Zero ? TimeSpan.Zero : left;
        }
    }

    public double Fraction
    {
        get
        {
            if (Progress.StartedAt is null) return 0.0;
            if (Def.Duration <= TimeSpan.Zero) return 1.0;
            return Math.Clamp((DateTimeOffset.UtcNow - Progress.StartedAt.Value) / Def.Duration, 0.0, 1.0);
        }
    }

    public string GroupKey => Def.GroupKey;
}
