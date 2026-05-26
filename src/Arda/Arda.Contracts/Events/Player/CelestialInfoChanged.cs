using Arda.Abstractions.Logs;

namespace Arda.World.Player.Events;

/// <summary>
/// Emitted when the game reports a new moon phase via
/// <c>ProcessSetCelestialInfo</c>. Carries both the raw token and the
/// classified <see cref="MoonPhase"/> enum.
/// </summary>
public readonly record struct CelestialInfoChanged(
    string? PreviousRawPhase,
    string RawPhase,
    MoonPhase Phase,
    string DisplayName,
    LogLineMetadata Metadata);
