namespace Mithril.Shared.Logging;

/// <summary>
/// L0.5 (#532) public surface for the system-signal pipe — typed
/// <see cref="SystemSignalLogLine"/> emissions covering the small fixed set
/// of session-level non-actor lines (<see cref="SystemSignalKind"/>).
/// <see cref="Mithril.Shared.Logging"/>'s <c>GameSessionService</c> consumes
/// this stream as the L0.5 migration reference.
/// </summary>
public interface ISystemSignalLogStream
{
    IAsyncEnumerable<SystemSignalLogLine> SubscribeAsync(CancellationToken ct);

    /// <summary>
    /// L1-facing replay-marker variant. See
    /// <see cref="ILocalPlayerLogStream.SubscribeWithReplayMarkerAsync"/>
    /// for the rationale. The default implementation throws — only L0.5
    /// implementors that feed L1 need to override.
    /// </summary>
    IAsyncEnumerable<LogEnvelope<SystemSignalLogLine>> SubscribeWithReplayMarkerAsync(CancellationToken ct)
        => throw new NotSupportedException(
            "Implementors must override to mint authoritative replay markers. " +
            "L0.5 only — the L1 driver (#550) consumes this. " +
            "Non-L0.5 implementors (e.g. test fakes) can keep the default thrower if they don't drive L1.");
}
