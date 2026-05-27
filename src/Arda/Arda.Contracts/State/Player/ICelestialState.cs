namespace Arda.World.Player;

/// <summary>
/// Read-only view of the current celestial (moon phase) state.
/// World-scoped — persists across area transitions, resets on character switch.
/// </summary>
public interface ICelestialState
{
    /// <summary>
    /// The raw moon-phase token from <c>ProcessSetCelestialInfo</c>
    /// (e.g. "WaxingCrescentMoon"), or <c>null</c> if not yet received.
    /// </summary>
    string? CurrentPhaseRaw { get; }

    /// <summary>Classified moon phase enum, or <see cref="MoonPhase.Unknown"/> for unrecognised tokens.</summary>
    MoonPhase Phase { get; }

    /// <summary>Human-readable display name for the current phase.</summary>
    string? DisplayName { get; }

    /// <summary>Timestamp of the most recent celestial observation.</summary>
    DateTimeOffset? MeasuredAt { get; }
}
