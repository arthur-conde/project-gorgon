namespace Mithril.WorldSim;

/// <summary>
/// One of three state-machine kinds (principle 10). Composers consume change
/// events (and/or domain frames from upstream composers) and emit domain frames
/// when their multi-frame pattern is satisfied. They <em>recognize</em> multi-
/// frame patterns in events PG already emits; they do not anticipate or
/// synthesize PG behavior.
///
/// <para>Composers chain via subscribe within a frame's resolution — they never
/// re-emit into the world's merger (principle 11 — per-frame resolution is a
/// finite DAG traversal; no cycles, no merger re-entry). Future-time emission
/// is the producer interface's job.</para>
/// </summary>
public interface IComposer
{
    /// <summary>
    /// Declared input event types (change events from folders, or domain frames
    /// from upstream composers). The world's resolution loop uses these to
    /// topologically order composer dispatch within a frame (principle 11).
    /// </summary>
    IReadOnlyCollection<Type> Subscribes { get; }

    /// <summary>
    /// Observe one event from a declared input type. May update internal pending
    /// state; may emit zero or more domain frames if the composer's pattern is
    /// satisfied. Emitted frames carry timestamps from the event(s) they
    /// correlated, not from <paramref name="clock"/>.<see cref="IWorldClock.Now"/>
    /// — this preserves the principle 1 contract that a frame's timestamp is the
    /// event time it represents.
    /// </summary>
    IReadOnlyList<Frame<object>> Observe(object eventPayload, IWorldClock clock);
}
