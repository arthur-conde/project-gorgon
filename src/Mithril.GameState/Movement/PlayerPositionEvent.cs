using Mithril.Shared.Logging;

namespace Mithril.GameState.Movement;

/// <summary>
/// Which Player.log line a <see cref="PlayerPositionEvent"/> was recovered
/// from. Both carry the local player's position in the same per-area
/// engine-unit frame; they differ in <em>when</em> the game emits them,
/// which matters for staleness.
/// </summary>
public enum PlayerPositionSource
{
    /// <summary><c>ProcessNewPosition</c> — emitted on teleport, zone change,
    /// and certain combat blinks. Sparse; the bulk of normal walking is
    /// unlogged.</summary>
    Movement,

    /// <summary><c>ProcessAddPlayer</c> — the local player being added to the
    /// scene at login / zone-in. This is the line the live replay window is
    /// seeded to, so it is observed at session start — the freshest anchor
    /// available before any teleport.</summary>
    Spawn,
}

/// <summary>
/// The local player's own world position, parsed from either
/// <c>LocalPlayer: ProcessNewPosition((X, Y, Z), …)</c> (teleport / zone-in /
/// combat blink) or <c>LocalPlayer: ProcessAddPlayer(…, System.String[],
/// (X, Y, Z), …)</c> (login / zone-in scene add). <see cref="X"/>/<see cref="Z"/>
/// is the ground plane and <see cref="Y"/> elevation, in the same per-area
/// engine-unit frame as <c>npcs.json</c> <c>Pos</c> / <c>landmarks.json</c>
/// <c>Loc</c>. Coordinates are <b>signed</b> — negative X/Z are common.
/// </summary>
public sealed record PlayerPositionEvent(
    DateTime Timestamp, double X, double Y, double Z, PlayerPositionSource Source)
    : LogEvent(Timestamp);
