using Arda.World.Player.Events;

namespace Legolas.Domain;

/// <summary>
/// Cached snapshot of the player's last position from the Arda event stream.
/// Replaces the retired <c>Mithril.GameState.Movement.PlayerPosition</c> record
/// within Legolas (same field set, different source enum).
/// </summary>
public sealed record TrackerFix(
    double X, double Y, double Z, DateTimeOffset MeasuredAt, PositionSource Source);
