namespace Arda.World.Player;

/// <summary>
/// Read-only view of the player's last-known position. Coordinates are the engine's
/// per-area frame: X/Z ground plane, Y elevation, signed. <c>null</c> before the
/// first <c>ProcessNewPosition</c> or <c>ProcessAddPlayer</c> of the session.
/// Consumers needing live change notifications subscribe to
/// <see cref="Events.PlayerPositionChanged"/> via <see cref="Arda.Dispatch.IDomainEventSubscriber"/>.
/// </summary>
public interface IPositionState
{
    double? X { get; }
    double? Y { get; }
    double? Z { get; }
}
