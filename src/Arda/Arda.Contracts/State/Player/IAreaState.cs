namespace Arda.World.Player;

/// <summary>
/// Read-only view of the current area state. Inject this to query the player's
/// current area after replay completes — avoids subscribing to <c>AreaChanged</c>
/// just to get a point-in-time snapshot.
/// </summary>
public interface IAreaState
{
    /// <summary>
    /// The area key the player is currently in (e.g. "AreaSerbule"), or <c>null</c>
    /// if the player is not in-world (character select, loading, disconnected).
    /// </summary>
    string? CurrentArea { get; }
}
