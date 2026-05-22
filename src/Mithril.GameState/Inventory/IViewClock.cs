namespace Mithril.GameState.Inventory;

/// <summary>
/// View-layer clock — the cross-source-composer's notion of "now" (#602,
/// design notebook Q5 resolution). <see cref="Now"/> is the max of the most
/// recently observed timestamps across both world buses the view subscribes
/// to. Used as the <see cref="TimeProvider"/> for the view's correlator so
/// the 5-second pairing TTL is replay-deterministic (advances by simulated
/// event time, NOT wall-clock).
///
/// <para>The view's clock is distinct from <see cref="Mithril.WorldSim.IWorldClock"/>
/// — each world owns its own clock advancing by its source's frames; the view
/// merges across both. Per the design notebook Q5: <em>"views derive a 'now'
/// from the max of the most-recently-observed frame timestamps across both
/// world buses."</em></para>
/// </summary>
public interface IViewClock
{
    /// <summary>
    /// View-time = <c>max(lastPlayerFrameTs, lastChatFrameTs)</c>. Reads
    /// before any frame has been observed return <see cref="DateTimeOffset.MinValue"/>
    /// — the correlator's TTL evaluation handles this trivially because no
    /// entries exist yet either.
    /// </summary>
    DateTimeOffset Now { get; }

    /// <summary>
    /// Tuple of the most recently observed timestamps from each side's bus —
    /// the per-side pre-merge values <see cref="Now"/> derives from. Exposed
    /// so tests + diagnostics can observe per-side advancement independently;
    /// production consumers use <see cref="Now"/>.
    /// </summary>
    (DateTimeOffset Player, DateTimeOffset Chat) Frames { get; }
}

/// <summary>
/// Concrete <see cref="IViewClock"/> the <see cref="InventoryView"/> uses.
/// Extends <see cref="TimeProvider"/> so the view's correlator can use it as
/// a drop-in for the default <see cref="TimeProvider.System"/> — this is what
/// makes the 5-second TTL gate replay-deterministic (the correlator stamps
/// entries with view-time on Add and evaluates TTL against view-time on
/// TryTake / DrainStale, so a replay that drains in 100 ms wall-clock still
/// sees event-time progressing exactly as in the source corpus).
/// </summary>
internal sealed class ViewClock : TimeProvider, IViewClock
{
    private readonly object _gate = new();
    private DateTimeOffset _lastPlayer = DateTimeOffset.MinValue;
    private DateTimeOffset _lastChat = DateTimeOffset.MinValue;

    public DateTimeOffset Now
    {
        get { lock (_gate) { return Max(_lastPlayer, _lastChat); } }
    }

    public (DateTimeOffset Player, DateTimeOffset Chat) Frames
    {
        get { lock (_gate) { return (_lastPlayer, _lastChat); } }
    }

    public override DateTimeOffset GetUtcNow() => Now;

    /// <summary>
    /// Update the player-side timestamp. The correlator's monotonic-time
    /// invariant requires a non-decreasing clock: we clamp to the previously
    /// observed value rather than going backwards, so a slightly out-of-order
    /// frame can't violate the bucket's enqueue-order property.
    /// </summary>
    public void AdvancePlayer(DateTimeOffset ts)
    {
        lock (_gate)
        {
            if (ts > _lastPlayer) _lastPlayer = ts;
        }
    }

    /// <summary>Update the chat-side timestamp. Monotonic-clamping mirrors
    /// <see cref="AdvancePlayer"/>.</summary>
    public void AdvanceChat(DateTimeOffset ts)
    {
        lock (_gate)
        {
            if (ts > _lastChat) _lastChat = ts;
        }
    }

    private static DateTimeOffset Max(DateTimeOffset a, DateTimeOffset b) => a >= b ? a : b;
}
