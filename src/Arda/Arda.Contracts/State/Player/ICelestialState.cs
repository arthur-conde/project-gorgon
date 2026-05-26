namespace Arda.World.Player;

/// <summary>
/// Read-only view of the current celestial (moon phase) state.
/// </summary>
public interface ICelestialState
{
    /// <summary>
    /// The raw moon-phase token from <c>ProcessSetCelestialInfo</c>
    /// (e.g. "WaxingCrescentMoon"), or <c>null</c> if not yet received.
    /// </summary>
    string? CurrentPhaseRaw { get; }
}
