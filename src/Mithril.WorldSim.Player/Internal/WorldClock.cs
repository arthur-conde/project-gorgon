namespace Mithril.WorldSim.Player.Internal;

/// <summary>
/// Mutable <see cref="IWorldClock"/> implementation owned by the world's
/// merger loop. Advances only through <see cref="Advance"/> (called when a
/// frame is applied) and <see cref="SetMode"/> (called when the world flips
/// between <see cref="WorldMode.Replaying"/> and <see cref="WorldMode.Live"/>).
///
/// <para>Not thread-safe by design — single-threaded mutator (the merger
/// loop). External readers see the publicly-immutable view (<see cref="Now"/>,
/// <see cref="Frame"/>, <see cref="Mode"/>) which the merger writes between
/// frame dispatches.</para>
/// </summary>
internal sealed class WorldClock : IWorldClock
{
    public DateTimeOffset Now { get; private set; } = DateTimeOffset.MinValue;
    public long Frame { get; private set; }
    public WorldMode Mode { get; private set; } = WorldMode.Replaying;

    /// <summary>
    /// Apply one frame's timestamp to the clock. <see cref="Now"/> takes the
    /// frame's timestamp; <see cref="Frame"/> ticks once. Late frames
    /// (timestamp earlier than the current <see cref="Now"/>) clamp to
    /// <see cref="Now"/> — per producer contract, late-stamped frames are
    /// "clamped + warned by the world."
    /// </summary>
    public void Advance(DateTimeOffset frameTimestamp)
    {
        if (frameTimestamp > Now)
        {
            Now = frameTimestamp;
        }
        Frame++;
    }

    public void SetMode(WorldMode mode) => Mode = mode;
}
