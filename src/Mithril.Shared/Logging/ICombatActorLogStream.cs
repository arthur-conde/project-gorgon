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
}
