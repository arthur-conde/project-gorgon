namespace Legolas.Domain;

/// <summary>
/// How the next measurement waypoint was chosen (#506). Distinct from
/// <see cref="Services.MultilaterationQuality"/> — guidance can start after
/// one <b>measurement spot</b> (a committed stand-and-read location), while a
/// treasure <b>solve</b> still needs three spots with position fixes.
/// </summary>
public enum MotherlodeGuidanceMode
{
    /// <summary>No committed measurement spots yet — no overlay waypoint.</summary>
    None,

    /// <summary>One spot read: heuristic spread (~30 m from the anchor).</summary>
    GenericSpread,

    /// <summary>Two or more spots: scored by GDOP reduction (OED-lite).</summary>
    GdopOptimal,
}

/// <summary>
/// Active guided next-measurement hint for Motherlode v2 (#506). A
/// <b>measurement spot</b> is one row in
/// <see cref="MotherlodeSession.LocationSamples"/> with at least one bound
/// distance for the active treasure — not a per-map click count (multi-map
/// mode may bind many maps at a single spot).
/// </summary>
public sealed record MotherlodeGuidance(
    WorldCoord SuggestedWorld,
    double ToleranceRadiusMetres,
    MotherlodeGuidanceMode Mode,
    string? RelativePhrase);
