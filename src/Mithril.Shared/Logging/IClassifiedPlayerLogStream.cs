namespace Mithril.Shared.Logging;

/// <summary>
/// L0.5 (#556) public surface for the unified classified pipe — the one
/// canonical ordered stream of every classified Player.log line, typed
/// as <see cref="IClassifiedPlayerLogLine"/>. The three per-Kind typed
/// pipes (<see cref="ILocalPlayerLogStream"/>, <see cref="ICombatActorLogStream"/>,
/// <see cref="ISystemSignalLogStream"/>) are derived views: a downstream
/// <c>PlayerLogPipeSplitter</c> subscribes to this stream and dispatches
/// envelopes to the matching typed pipe.
///
/// <para>Cross-pipe-ordering-sensitive consumers (Pin, Weather, Position —
/// #556) subscribe via the L1 driver to <see cref="IClassifiedPlayerLogLine"/>
/// envelopes here. Consumers needing only one Kind subscribe via the
/// typed pipes.</para>
///
/// <para>The driving implementation is <see cref="PlayerLogClassifier"/>;
/// it owns the L0 subscription, classification + cheap-discard hot path,
/// the unified-pipe replay buffer, and the lazy-start /
/// stop-on-last-unsubscribe lifecycle (including the #547 restart-race
/// fix).</para>
/// </summary>
public interface IClassifiedPlayerLogStream
{
    /// <summary>
    /// L1-facing subscription. Yields each classified line wrapped in
    /// <see cref="LogEnvelope{T}"/> so the structural
    /// <see cref="LogEnvelope{T}.IsReplay"/> bit is sourced authoritatively
    /// from the unified pipe's own session-replay-vs-live boundary — the
    /// L1 driver forwards the bit unchanged.
    /// </summary>
    IAsyncEnumerable<LogEnvelope<IClassifiedPlayerLogLine>>
        SubscribeWithReplayMarkerAsync(CancellationToken ct);
}
