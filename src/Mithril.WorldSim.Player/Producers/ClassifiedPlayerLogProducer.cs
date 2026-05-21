using System.Runtime.CompilerServices;
using Mithril.Shared.Logging;

namespace Mithril.WorldSim.Player.Producers;

/// <summary>
/// Bridges the L1 classified Player.log pipe (<see cref="IClassifiedPlayerLogStream"/>)
/// into the world simulator as an <see cref="IFrameProducer{TPayload}"/> over
/// <see cref="IClassifiedPlayerLogLine"/>. The replay-vs-live boundary on the
/// envelope's <see cref="LogEnvelope{T}.IsReplay"/> flag drives the world's
/// <see cref="WorldMode"/> transition via <see cref="IModeAwareFrameProducer{TPayload}"/>
/// — <see cref="ReachedLive"/> completes the moment the producer reads the
/// first non-replay envelope from the underlying stream.
///
/// <para>The frame's payload is the line itself; folders inspect the
/// <see cref="IClassifiedPlayerLogLine"/> discriminator (cast to one of
/// <c>LocalPlayerLogLine</c> / <c>CombatActorLogLine</c> / <c>SystemSignalLogLine</c>)
/// to route within the Player.log world during Phase 1+ folder migrations.
/// Phase 0 ships no folders, so frames flow through the merger / clock only.</para>
/// </summary>
public sealed class ClassifiedPlayerLogProducer : IFrameProducer<IClassifiedPlayerLogLine>, IModeAwareFrameProducer<IClassifiedPlayerLogLine>
{
    private readonly IClassifiedPlayerLogStream _stream;
    private readonly TaskCompletionSource _reachedLive = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public ClassifiedPlayerLogProducer(IClassifiedPlayerLogStream stream)
    {
        _stream = stream ?? throw new ArgumentNullException(nameof(stream));
    }

    /// <summary>
    /// The single producer registered on PlayerWorld today carries priority 0.
    /// Additional producers (future filesystem-reconcile, etc.) declare
    /// strictly-higher numeric priorities so the L1 pipe always wins
    /// timestamp ties — matching the L1 producer's source-Sequence ordering
    /// for native Player.log frames.
    /// </summary>
    public int Priority => 0;

    public Task ReachedLive => _reachedLive.Task;

    public async IAsyncEnumerable<Frame<IClassifiedPlayerLogLine>> SubscribeAsync(
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

            yield return new Frame<IClassifiedPlayerLogLine>(
                envelope.Payload.Timestamp,
                envelope.Payload);
        }

        // Stream ended without ever flipping (degenerate — replay-only file
        // tail with no live channel). Mark live anyway so the world isn't
        // stuck in Replaying forever after exhaustion.
        _reachedLive.TrySetResult();
    }
}
