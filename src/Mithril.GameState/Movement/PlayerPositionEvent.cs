using Mithril.Shared.Logging;

namespace Mithril.GameState.Movement;

/// <summary>
/// The local player's own world position, parsed from the
/// <c>LocalPlayer: ProcessNewPosition((X, Y, Z), …)</c> line PG emits on
/// teleport / zone-in / certain combat blinks (sparse — ~10/day, not a
/// per-tick position feed). <see cref="X"/>/<see cref="Z"/> is the ground
/// plane and <see cref="Y"/> elevation, in the same per-area engine-unit
/// frame as <c>npcs.json</c> <c>Pos</c> / <c>landmarks.json</c> <c>Loc</c>.
/// Coordinates are <b>signed</b> — negative X/Z are common.
/// </summary>
public sealed record PlayerPositionEvent(DateTime Timestamp, double X, double Y, double Z)
    : LogEvent(Timestamp);
