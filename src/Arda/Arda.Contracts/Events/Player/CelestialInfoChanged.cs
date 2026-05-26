using Arda.Abstractions.Logs;

namespace Arda.World.Player.Events;

/// <summary>
/// Emitted when the game reports a new moon phase via
/// <c>ProcessSetCelestialInfo</c>. Carries the raw token verbatim —
/// canonical <see cref="MoonPhase"/> mapping is consumer-side.
/// </summary>
public readonly record struct CelestialInfoChanged(
    string? PreviousRawPhase,
    string RawPhase,
    LogLineMetadata Metadata);
