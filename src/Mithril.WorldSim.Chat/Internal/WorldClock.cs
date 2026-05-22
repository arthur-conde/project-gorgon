namespace Mithril.WorldSim.Chat.Internal;

/// <summary>
/// Mutable <see cref="IWorldClock"/> implementation owned by the chat world's
/// merger loop. Same shape as the Player-side world clock — single-threaded
/// mutator, frame-driven advancement, mode-flip via <see cref="SetMode"/>. The
/// two worlds intentionally duplicate this small class rather than share a
/// base, matching principle 2's "sealed at the bus" framing: a clock bug in
/// one world cannot accidentally couple to the other.
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
