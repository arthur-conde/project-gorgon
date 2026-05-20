namespace Mithril.Shared.Logging;

/// <summary>
/// L0.5 (#532) public surface for the LocalPlayer pipe — typed
/// <see cref="LocalPlayerLogLine"/> emissions classified from L0's
/// <see cref="IPlayerLogStream"/> Player.log feed. All current GameState
/// state-rebuilders and reactive module ingestion services that today
/// consume <see cref="IPlayerLogStream"/> + match <c>LocalPlayer:</c>
/// will migrate to this stream as part of #511 deliverable 5; until then
/// L0.5 ships dark alongside the existing <see cref="IPlayerLogStream"/>
/// surface.
/// </summary>
public interface ILocalPlayerLogStream
{
    IAsyncEnumerable<LocalPlayerLogLine> SubscribeAsync(CancellationToken ct);

    /// <summary>
    /// L1-facing variant: yields the same envelopes as
    /// <see cref="SubscribeAsync"/> wrapped in <see cref="LogEnvelope{T}"/>
    /// so each carries the structural <c>IsReplay</c> bit the L0.5 router
    /// can answer authoritatively (a yield from the per-pipe replay
    /// snapshot vs. a live emission from the bounded-channel branch).
    /// Added in #550 PR 1 so L1's <see cref="LogEnvelope{T}.IsReplay"/>
    /// flag is sourced from the upstream's own boundary rather than a
    /// sync-vs-async heuristic. Existing consumers (pre-L1) continue to
    /// use <see cref="SubscribeAsync"/> unchanged.
    /// <para>The default implementation throws — implementors that drive
    /// L1 (today: <see cref="PlayerLogActorRouter"/>) MUST override.
    /// Non-L0.5 implementors (test fakes that never feed L1) can keep
    /// the default thrower.</para>
    /// </summary>
    IAsyncEnumerable<LogEnvelope<LocalPlayerLogLine>> SubscribeWithReplayMarkerAsync(CancellationToken ct)
        => throw new NotSupportedException(
            "Implementors must override to mint authoritative replay markers. " +
            "L0.5 only — the L1 driver (#550) consumes this. " +
            "Non-L0.5 implementors (e.g. test fakes) can keep the default thrower if they don't drive L1.");
}
