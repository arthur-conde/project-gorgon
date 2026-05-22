using System.Runtime.CompilerServices;
using Mithril.Shared.Logging;

namespace Mithril.WorldSim.Player.Producers;

/// <summary>
/// Drives PlayerWorld's clock from the L1 classified Player.log pipe
/// (<see cref="IClassifiedPlayerLogStream"/>). Emits one
/// <see cref="Frame{TPayload}"/> of <see cref="WorldClockTick"/> per source
/// envelope, stamped with the envelope's payload timestamp — so the world
/// clock advances at the source-stream cadence whether or not any other
/// folder happens to consume the underlying line.
///
/// <para>The owned folder (<see cref="Internal.WorldClockTickFolder"/>) turns
/// each tick into a <see cref="CalendarTimeAdvanced"/> change event,
/// deduplicated within a wall-clock second (principle 13). Module-side
/// schedulers subscribe to <c>CalendarTimeAdvanced</c> on the world's bus;
/// Gandalf's planned scheduler-collapse alarms (migration item #12) are
/// the canonical downstream consumer.</para>
///
/// <para>The replay-vs-live boundary on the envelope's
/// <see cref="LogEnvelope{T}.IsReplay"/> flag drives the world's
/// <see cref="WorldMode"/> transition via
/// <see cref="IModeAwareFrameProducer{TPayload}"/> — <see cref="ReachedLive"/>
/// completes the moment the producer reads the first non-replay envelope
/// from the underlying stream, matching the historical
/// <c>ClassifiedPlayerLogProducer</c>'s mode-signalling shape exactly so
/// the world's Replaying → Live transition continues to work as before.</para>
///
/// <para>This producer carries ONLY the clock-tick payload; the per-line
/// payloads (LocalPlayer / CombatActor / SystemSignal) are consumed by
/// per-folder producers landing in Phase 1+ migration issues (#618 and
/// friends) — each tails the same L1 pipe and discriminates on the line
/// kind it cares about. See <a href="https://github.com/moumantai-gg/mithril/issues/644">#644</a>
/// for the per-folder-producer ratification and
/// <a href="https://github.com/moumantai-gg/mithril/issues/655">#655</a>
/// for this reshape.</para>
/// </summary>
public sealed class WorldClockTickProducer : IFrameProducer<WorldClockTick>, IModeAwareFrameProducer<WorldClockTick>
{
    private readonly IClassifiedPlayerLogStream _stream;
    private readonly TaskCompletionSource _reachedLive = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public WorldClockTickProducer(IClassifiedPlayerLogStream stream)
    {
        _stream = stream ?? throw new ArgumentNullException(nameof(stream));
    }

    /// <summary>
    /// The clock-tick producer is the lowest-priority producer on PlayerWorld
    /// (priority 0). Per-folder producers (Phase 1+) register at strictly-
    /// higher numeric priorities so any tied-timestamp folder frame is
    /// dispatched BEFORE the corresponding tick — keeping
    /// <see cref="CalendarTimeAdvanced"/> emission a strict trailing edge
    /// to the same-timestamp folder work. (The L1 contract gives a 1-second
    /// timestamp resolution, so identical timestamps across producers are
    /// the common case during busy stretches.)
    /// </summary>
    public int Priority => 0;

    public Task ReachedLive => _reachedLive.Task;

    public async IAsyncEnumerable<Frame<WorldClockTick>> SubscribeAsync(
        [EnumeratorCancellation] CancellationToken ct)
    {
        await foreach (var envelope in _stream.SubscribeWithReplayMarkerAsync(ct).ConfigureAwait(false))
        {
            // Signal "reached live" BEFORE yielding the first live frame so
            // the world flips Mode → Live before dispatching it. The L1
            // contract guarantees IsReplay flips false-onward only once and
            // never re-arms (per LogEnvelope.IsReplay doc), so TrySetResult
            // is idempotent past that boundary.
            if (!envelope.IsReplay)
            {
                _reachedLive.TrySetResult();
            }

            yield return new Frame<WorldClockTick>(
                envelope.Payload.Timestamp,
                new WorldClockTick(envelope.Payload.Timestamp));
        }

        // Stream ended without ever flipping (degenerate — replay-only file
        // tail with no live channel). Mark live anyway so the world isn't
        // stuck in Replaying forever after exhaustion.
        _reachedLive.TrySetResult();
    }
}
