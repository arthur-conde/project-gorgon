using Arda.World.Player;

namespace Legolas.Domain;

/// <summary>
/// Which reference tier a <see cref="MotherlodeBearing"/> anchored to. Ordered
/// by positional reliability (lowest = most trustworthy): a spot the player
/// physically stood on has zero standoff; their own map pin is an exact
/// map-read; the landmark/NPC gazetteer carries a ~5–15 m systematic standoff.
/// Used only to break exact distance ties (and to feed future confidence
/// copy) — proximity, not tier, is what makes the phrase useful, so the
/// nearest reference wins regardless of tier (#113 Layer 1).
/// </summary>
public enum MotherlodeReferenceTier
{
    MeasuredSpot,
    MapPin,
    Gazetteer,
}

/// <summary>
/// A solved Motherlode treasure phrased relative to a known reference:
/// "<c>≈ 340 m NE of Serbule Keep</c>". Calibration-free — the engine-unit
/// world frame is internally consistent, so distance and bearing are exact
/// even when the area has no map calibration. The treasure's solved coordinate
/// itself is exact; only the on-map dot (#113 Layer 5) inherits the projector
/// warp.
/// </summary>
public sealed record MotherlodeBearing(
    double DistanceMetres,
    CardinalDirection Direction,
    string ReferenceName,
    MotherlodeReferenceTier Tier)
{
    /// <summary>Player-facing phrase. Distance rounds to the nearest 10 m
    /// (engine-unit ≈ metre, bounded not exact — #488); a treasure essentially
    /// on top of the reference drops the bearing.</summary>
    public string ToDisplayString()
    {
        var d = (int)Math.Round(DistanceMetres / 10.0, MidpointRounding.AwayFromZero) * 10;
        return d <= 0
            ? $"at {ReferenceName}"
            : $"≈ {d} m {Direction} of {ReferenceName}";
    }
}

/// <summary>
/// Pure presentation transform (#113 Layer 1): turns a solved treasure's raw
/// world coordinate — unactionable on its own, since Project Gorgon has no
/// in-game coordinate readout — into the nearest recognizable reference plus a
/// compass bearing and distance.
///
/// <para>Reference candidates come from three tiers, all in the same per-area
/// engine-unit frame: the player's own measured spots (zero standoff), their
/// map pins (exact map-read), and the area's landmark/NPC gazetteer (richest
/// names, slight standoff). The globally nearest candidate wins — a far
/// "reliable" reference yields a useless phrase, and the gazetteer standoff is
/// negligible against a navigation-scale hike. Tier only breaks exact ties.
/// Gracefully degrades: pass an empty gazetteer when the area doesn't match
/// and the spots/pins still anchor; null out only when nothing is known.</para>
/// </summary>
public static class MotherlodeReferenceLocator
{
    /// <param name="solved">The treasure's solved world coordinate.</param>
    /// <param name="measuredSpots">The shared Pᵢ set
    /// (<see cref="MotherlodeSession.LocationSamples"/> order); fix-less rows
    /// (Confidence ≤ 0) are skipped but still consume a "spot #N" index so the
    /// label matches the readings the player took.</param>
    /// <param name="pins">Current-area player map pins.</param>
    /// <param name="gazetteer">Current-area landmark/NPC references
    /// (<see cref="CalibrationReference"/>); empty when the area is unknown or
    /// has no reference data.</param>
    /// <returns>The nearest reference, or <c>null</c> when no reference of any
    /// tier is available.</returns>
    public static MotherlodeBearing? Nearest(
        WorldCoord solved,
        IReadOnlyList<MotherlodePositionSample> measuredSpots,
        IReadOnlyCollection<MapPinEntry> pins,
        IReadOnlyList<CalibrationReference> gazetteer)
    {
        MotherlodeBearing? best = null;
        var bestDist = double.MaxValue;

        void Consider(double wx, double wz, string name, MotherlodeReferenceTier tier)
        {
            var east = solved.X - wx;     // ΔX, vector reference → treasure
            var north = solved.Z - wz;    // ΔZ
            var dist = Math.Sqrt(east * east + north * north);
            // Nearest wins; an exact tie resolves to the more reliable (lower) tier.
            if (best is not null &&
                !(dist < bestDist - 1e-9 ||
                  (Math.Abs(dist - bestDist) <= 1e-9 && tier < best.Tier)))
                return;
            best = new MotherlodeBearing(
                dist, CardinalDirectionExtensions.FromBearing(east, north), name, tier);
            bestDist = dist;
        }

        for (var i = 0; i < measuredSpots.Count; i++)
        {
            var s = measuredSpots[i];
            if (s.Confidence <= 0) continue;   // fix-less placeholder — keeps the index
            Consider(s.World.X, s.World.Z, $"your spot #{i + 1}", MotherlodeReferenceTier.MeasuredSpot);
        }

        foreach (var p in pins)
        {
            var name = string.IsNullOrWhiteSpace(p.Label)
                ? $"your {p.Appearance()} pin"
                : $"\"{p.Label}\"";
            Consider(p.X, p.Z, name, MotherlodeReferenceTier.MapPin);
        }

        foreach (var g in gazetteer)
            Consider(g.World.X, g.World.Z, g.Name, MotherlodeReferenceTier.Gazetteer);

        return best;
    }
}
