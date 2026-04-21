using System.Text.Json.Serialization;

namespace Gandalf.Domain;

public enum TimerState { Idle, Running, Done }

public sealed class GandalfTimer
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Name { get; set; } = "";
    public TimeSpan Duration { get; set; }
    public string Region { get; set; } = "";
    public string Map { get; set; } = "";
    public DateTimeOffset? StartedAt { get; set; }
    public DateTimeOffset? CompletedAt { get; set; }

    [JsonIgnore]
    public TimerState State
    {
        get
        {
            if (StartedAt is null) return TimerState.Idle;
            if (CompletedAt is not null) return TimerState.Done;
            if (DateTimeOffset.UtcNow - StartedAt.Value >= Duration) return TimerState.Done;
            return TimerState.Running;
        }
    }

    [JsonIgnore]
    public TimeSpan Remaining
    {
        get
        {
            if (StartedAt is null) return Duration;
            var left = Duration - (DateTimeOffset.UtcNow - StartedAt.Value);
            return left < TimeSpan.Zero ? TimeSpan.Zero : left;
        }
    }

    [JsonIgnore]
    public double Fraction
    {
        get
        {
            if (StartedAt is null) return 0.0;
            if (Duration <= TimeSpan.Zero) return 1.0;
            return Math.Clamp((DateTimeOffset.UtcNow - StartedAt.Value) / Duration, 0.0, 1.0);
        }
    }

    [JsonIgnore]
    public string GroupKey => string.IsNullOrWhiteSpace(Map)
        ? Region
        : $"{Region} > {Map}";

    public void Start()
    {
        StartedAt = DateTimeOffset.UtcNow;
        CompletedAt = null;
    }

    public void Restart()
    {
        StartedAt = DateTimeOffset.UtcNow;
        CompletedAt = null;
    }
}
