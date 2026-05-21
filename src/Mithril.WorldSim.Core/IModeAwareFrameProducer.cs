namespace Mithril.WorldSim;

/// <summary>
/// Optional companion to <see cref="IFrameProducer{TPayload}"/> for producers
/// with an explicit "draining recorded backlog vs blocking on live tail"
/// boundary (e.g., the L1 classified-pipe reader's <c>IsReplay</c> flip — see
/// <c>Mithril.Shared.Logging.LogEnvelope{T}.IsReplay</c>).
///
/// <para>The world's clock flips from <see cref="WorldMode.Replaying"/> to
/// <see cref="WorldMode.Live"/> once every registered mode-aware producer's
/// <see cref="ReachedLive"/> task has completed. Non-mode-aware producers
/// are treated as live from t=0 (they have no replay phase to drain).
/// Producers signal catch-up by completing this task; the world polls
/// <see cref="Task.IsCompleted"/> between frame dispatches (principle 12 —
/// "the mechanism a world uses to detect 'drained, now on the live tail'
/// is producer-implementation-specific; the world just observes the
/// result").</para>
///
/// <para>Implementers must complete the task BEFORE yielding the first live
/// frame from <see cref="IFrameProducer{TPayload}.SubscribeAsync"/>, so that
/// the world flips mode before dispatching that first live frame.</para>
/// </summary>
/// <typeparam name="TPayload">
/// The frame payload type — must match the underlying
/// <see cref="IFrameProducer{TPayload}"/>.
/// </typeparam>
public interface IModeAwareFrameProducer<TPayload> : IFrameProducer<TPayload>
{
    /// <summary>
    /// Completes when the producer has drained its replay backlog and is now
    /// blocking on its live source-stream tail. The task transitions only
    /// once — from incomplete to completed — and is never re-armed. If the
    /// underlying source has no replay phase (live from the start), the
    /// implementer should complete the task immediately at construction.
    /// </summary>
    Task ReachedLive { get; }
}
