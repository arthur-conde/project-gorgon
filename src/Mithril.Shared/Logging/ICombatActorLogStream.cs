namespace Mithril.Shared.Logging;

/// <summary>
/// L0.5 (#532) public surface for the combat-actor pipe — typed
/// <see cref="CombatActorLogLine"/> emissions. Reserved per the design;
/// no consumer subscribes today. Future combat consumers slot in here
/// without moving the channel boundary.
/// </summary>
public interface ICombatActorLogStream
{
    IAsyncEnumerable<CombatActorLogLine> SubscribeAsync(CancellationToken ct);

    /// <summary>
    /// L1-facing replay-marker variant. See
    /// <see cref="ILocalPlayerLogStream.SubscribeWithReplayMarkerAsync"/>
    /// for the rationale. The default implementation throws — only L0.5
    /// implementors that feed L1 need to override.
    /// </summary>
    IAsyncEnumerable<LogEnvelope<CombatActorLogLine>> SubscribeWithReplayMarkerAsync(CancellationToken ct)
        => throw new NotSupportedException(
            "Implementors must override to mint authoritative replay markers. " +
            "L0.5 only — the L1 driver (#550) consumes this. " +
            "Non-L0.5 implementors (e.g. test fakes) can keep the default thrower if they don't drive L1.");
}
