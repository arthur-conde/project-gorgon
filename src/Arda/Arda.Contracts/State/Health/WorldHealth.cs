namespace Arda.Contracts.State.Health;

/// <summary>
/// Per-driver health snapshot: current timestamp from the log stream,
/// frame count, replay/live mode, and drift from wall-clock.
/// </summary>
public sealed record WorldHealth(
    DateTimeOffset? LastTimestamp,
    long FrameCount,
    WorldMode Mode,
    TimeSpan Drift);

/// <summary>Mode of a world driver.</summary>
public enum WorldMode
{
    /// <summary>Processing historical log lines from before Mithril started.</summary>
    Replaying,

    /// <summary>Caught up; tailing live log output.</summary>
    Live
}
