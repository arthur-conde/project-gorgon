using Mithril.Shared.Logging;

namespace Mithril.GameState.Celestial.Parsing;

/// <summary>
/// The local player's current lunar phase, parsed from
/// <c>LocalPlayer: ProcessSetCelestialInfo(&lt;token&gt;)</c>. PG emits this on
/// login and again whenever the phase rolls over. <see cref="RawPhase"/> is
/// the verbatim log token; <see cref="Phase"/> is its canonical mapping
/// (<see cref="MoonPhase.Unknown"/> if the token is unrecognised — the raw
/// string is still authoritative).
/// </summary>
public sealed record CelestialInfoEvent(DateTime Timestamp, MoonPhase Phase, string RawPhase)
    : LogEvent(Timestamp);
