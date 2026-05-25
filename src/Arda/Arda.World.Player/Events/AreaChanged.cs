using Arda.Abstractions.Logs;

namespace Arda.World.Player.Events;

/// <summary>
/// Emitted when the player transitions between areas (or leaves the world).
/// Confirmed only after the two-phase <c>LOADING_LEVEL</c> + <c>InitializingArea</c>
/// sequence completes, or immediately on area-clearing events.
/// </summary>
public readonly record struct AreaChanged(
    string? PreviousArea,
    string? CurrentArea,
    LogLineMetadata Metadata);
