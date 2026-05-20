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
}
