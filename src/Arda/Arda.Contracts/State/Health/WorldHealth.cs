namespace Arda.Contracts.State.Health;

/// <summary>
/// Per-driver health snapshot.
/// <para>
/// <see cref="Drift"/> is the wall-clock age of the tailer's last poll —
/// "when did this tailer last look at the file". This is distinct from
/// <see cref="LastLogTimestamp"/>, which is the most recent in-stream
/// timestamp emitted as a domain event. The two diverge whenever the game
/// produces no log lines (AFK, quiet chat channel, off-hours) — drift stays
/// near zero because the tailer keeps polling, while LastLogTimestamp lags.
/// </para>
/// <para>
/// Issue #856 redefined Drift away from "now − LastLogTimestamp" because
/// the old metric false-fired on quiet chat channels: an inactive channel
/// produces no chat events, so the chat tailer looked broken to consumers.
/// The new metric is bounded by the poll interval and means "the polling
/// loop has stopped" — a real liveness signal.
/// </para>
/// </summary>
public sealed record WorldHealth(
    DateTimeOffset? LastLogTimestamp,
    long FrameCount,
    WorldMode Mode,
    TimeSpan Drift)
{
    /// <summary>
    /// Drift beyond this threshold in <see cref="WorldMode.Live"/> means
    /// the tailer's poll loop has gone silent for longer than expected and
    /// the driver flips to <see cref="WorldMode.Stalled"/>. Default poll
    /// interval is ~250–500ms so 5s is ~10 missed polls.
    /// </summary>
    public static readonly TimeSpan DriftWarningThreshold = TimeSpan.FromSeconds(5);
}

/// <summary>Mode of a world driver.</summary>
public enum WorldMode
{
    /// <summary>Processing historical log lines from before Mithril started.</summary>
    Replaying,

    /// <summary>Caught up; tailing live log output; pulse observed within threshold.</summary>
    Live,

    /// <summary>
    /// Was <see cref="Live"/>; tailer-poll pulse has gone silent past
    /// <see cref="WorldHealth.DriftWarningThreshold"/>. The polling loop
    /// appears to have stopped — distinct from <see cref="Halted"/> (grammar
    /// break) and <see cref="Replaying"/> (still catching up).
    /// </summary>
    Stalled,

    /// <summary>
    /// Halted on a <c>GrammarException</c>. The driver has stopped consuming;
    /// the world model is frozen at the last successfully-processed line.
    /// </summary>
    Halted
}
