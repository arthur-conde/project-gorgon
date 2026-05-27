using Arda.Abstractions.Logs;

namespace Arda.World.Player.Events;

/// <summary>
/// Source of a <see cref="PlayerPositionChanged"/> event.
/// </summary>
public enum PositionSource
{
    /// <summary><c>ProcessNewPosition</c> — teleport, zone change, combat blink. Sparse.</summary>
    Movement,

    /// <summary><c>ProcessAddPlayer</c> — local player added to scene at login/zone-in.</summary>
    Spawn,
}

/// <summary>
/// Tier 1 state event for player position changes. Emitted on
/// <c>ProcessNewPosition</c> (teleport/zone/blink) and the local player's
/// <c>ProcessAddPlayer</c> (login/zone-in spawn). Coordinates are the engine's
/// per-area frame: X/Z ground plane, Y elevation, signed.
/// Primary consumers: Legolas, Palantir.
/// </summary>
public readonly record struct PlayerPositionChanged(
    double X,
    double Y,
    double Z,
    PositionSource Source,
    LogLineMetadata Metadata);
