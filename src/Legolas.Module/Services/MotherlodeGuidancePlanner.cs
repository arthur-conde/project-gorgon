using Arda.World.Player;
using Legolas.Domain;

namespace Legolas.Services;

/// <summary>
/// #506: chooses the next measurement waypoint from committed spots. Gates on
/// <paramref name="measurementSpotCount"/> (spots with ≥1 bound distance), not
/// on the three-spot solve minimum — that stays in
/// <see cref="MultilaterationSolver"/>.
/// </summary>
public static class MotherlodeGuidancePlanner
{
    // Matches the solver's "~30 m distinct-spot" copy and #488 field notes.
    public const double DefaultSpreadMetres = 30;

    private const int CandidateBearings = 16;
    private const double MinToleranceMetres = 8;
    private const double MaxToleranceMetres = 24;

    /// <summary>
    /// Plan the next stand-and-read location for <paramref name="slotIndex"/>.
    /// Returns <c>null</c> when there are no committed measurement spots.
    /// </summary>
    public static MotherlodeGuidance? Plan(
        MotherlodeSession session,
        int slotIndex,
        int measurementSpotCount,
        IReadOnlyCollection<MapPinEntry> pins,
        IReadOnlyList<CalibrationReference> gazetteer)
    {
        if (measurementSpotCount <= 0 || slotIndex < 0 || slotIndex >= session.Surveys.Count)
            return null;

        var samples = BuildSamples(session, slotIndex);
        if (samples.Count == 0) return null;

        var anchor = LastSpotWithFix(session);
        var ax = anchor?.World.X ?? samples[^1].X;
        var az = anchor?.World.Z ?? samples[^1].Z;

        WorldCoord suggested;
        MotherlodeGuidanceMode mode;
        double scoreGdop;

        if (measurementSpotCount < 2)
        {
            mode = MotherlodeGuidanceMode.GenericSpread;
            var (dx, dz) = GenericSpreadOffset(samples);
            suggested = new WorldCoord(ax + dx, 0, az + dz);
            scoreGdop = double.PositiveInfinity;
        }
        else
        {
            mode = MotherlodeGuidanceMode.GdopOptimal;
            if (ProvisionalTarget(session, slotIndex, samples) is not { } target)
            {
                mode = MotherlodeGuidanceMode.GenericSpread;
                var (dx, dz) = GenericSpreadOffset(samples);
                suggested = new WorldCoord(ax + dx, 0, az + dz);
                scoreGdop = double.PositiveInfinity;
            }
            else
            {
                var (sx, sz, gdop) = BestCandidate(samples, target, ax, az);
                suggested = new WorldCoord(sx, 0, sz);
                scoreGdop = gdop;
            }
        }

        var tolerance = ToleranceRadiusMetres(scoreGdop, mode);
        var phrase = MotherlodeReferenceLocator.Nearest(
            suggested, session.LocationSamples, pins, gazetteer)?.ToDisplayString();

        return new MotherlodeGuidance(suggested, tolerance, mode, phrase);
    }

    /// <summary>Spots that have at least one bound distance for this slot.</summary>
    public static int CountMeasurementSpots(MotherlodeSession session, int slotIndex)
    {
        if (slotIndex < 0 || slotIndex >= session.Surveys.Count) return 0;
        var rows = session.LocationSamples.Count;
        if (rows == 0) return 0;
        var count = 0;
        var s = session.Surveys[slotIndex];
        for (var row = 0; row < rows && row < s.DistancesByLocation.Count; row++)
            if (s.DistancesByLocation[row] > 0) count++;
        return count;
    }

    private static List<MultilaterationSample> BuildSamples(MotherlodeSession session, int slotIndex)
    {
        var s = session.Surveys[slotIndex];
        var list = new List<MultilaterationSample>();
        for (var row = 0; row < session.LocationSamples.Count && row < s.DistancesByLocation.Count; row++)
        {
            var loc = session.LocationSamples[row];
            var dist = s.DistancesByLocation[row];
            if (loc.Confidence <= 0 || dist <= 0) continue;
            list.Add(new MultilaterationSample(loc.World.X, loc.World.Z, dist, loc.Confidence));
        }
        return list;
    }

    private static MotherlodePositionSample? LastSpotWithFix(MotherlodeSession session)
    {
        for (var i = session.LocationSamples.Count - 1; i >= 0; i--)
            if (session.LocationSamples[i].Confidence > 0)
                return session.LocationSamples[i];
        return null;
    }

    private static (double Dx, double Dz) GenericSpreadOffset(IReadOnlyList<MultilaterationSample> samples)
    {
        // With one spot any bearing at DefaultSpreadMetres helps; with two+ but
        // no provisional target, aim ~perpendicular to the spot chord.
        if (samples.Count >= 2)
        {
            var dx = samples[^1].X - samples[0].X;
            var dz = samples[^1].Z - samples[0].Z;
            var len = Math.Sqrt(dx * dx + dz * dz);
            if (len > 1e-6)
            {
                // Perpendicular in XZ: (-dz, dx) normalized.
                var px = -dz / len;
                var pz = dx / len;
                return (px * DefaultSpreadMetres, pz * DefaultSpreadMetres);
            }
        }

        // Default: NE-ish from the anchor when we have no chord.
        const double invSqrt2 = 0.7071067811865476;
        return (DefaultSpreadMetres * invSqrt2, DefaultSpreadMetres * invSqrt2);
    }

    private static (double X, double Z)? ProvisionalTarget(
        MotherlodeSession session, int slotIndex, IReadOnlyList<MultilaterationSample> samples)
    {
        var survey = session.Surveys[slotIndex];
        if (survey.SolvedWorld is { } solved)
            return (solved.X, solved.Z);

        if (samples.Count >= 3)
        {
            var r = new MultilaterationSolver().Solve(samples);
            if (r.Point is { } p) return (p.X, p.Z);
        }

        if (samples.Count == 2)
            return TwoCircleMidpoint(samples[0], samples[1]);

        return null;
    }

    private static (double X, double Z)? TwoCircleMidpoint(
        MultilaterationSample a, MultilaterationSample b)
    {
        var x1 = a.X; var z1 = a.Z; var r1 = a.Distance;
        var x2 = b.X; var z2 = b.Z; var r2 = b.Distance;
        var dx = x2 - x1;
        var dz = z2 - z1;
        var d = Math.Sqrt(dx * dx + dz * dz);
        if (d < 1e-6 || d > r1 + r2 + 1e-6 || d < Math.Abs(r1 - r2) - 1e-6)
            return null;

        var aLen = (r1 * r1 - r2 * r2 + d * d) / (2 * d);
        var h2 = r1 * r1 - aLen * aLen;
        if (h2 < -1e-6) return null;
        var h = h2 <= 0 ? 0 : Math.Sqrt(h2);
        var mx = x1 + aLen * dx / d;
        var mz = z1 + aLen * dz / d;
        var px = -dz / d;
        var pz = dx / d;
        var ix1 = mx + h * px;
        var iz1 = mz + h * pz;
        var ix2 = mx - h * px;
        var iz2 = mz - h * pz;
        return ((ix1 + ix2) * 0.5, (iz1 + iz2) * 0.5);
    }

    private static (double X, double Z, double Gdop) BestCandidate(
        IReadOnlyList<MultilaterationSample> samples,
        (double X, double Z) target,
        double anchorX,
        double anchorZ)
    {
        var bestX = anchorX + DefaultSpreadMetres;
        var bestZ = anchorZ;
        var bestGdop = double.PositiveInfinity;

        for (var i = 0; i < CandidateBearings; i++)
        {
            var angle = i * (2 * Math.PI / CandidateBearings);
            var cx = anchorX + DefaultSpreadMetres * Math.Sin(angle);
            var cz = anchorZ + DefaultSpreadMetres * Math.Cos(angle);
            var dist = Math.Sqrt((cx - target.X) * (cx - target.X) + (cz - target.Z) * (cz - target.Z));
            var trial = new List<MultilaterationSample>(samples.Count + 1);
            trial.AddRange(samples);
            trial.Add(new MultilaterationSample(cx, cz, dist, 0.8));
            if (new MultilaterationSolver().Solve(trial) is not { } r || r.Point is null)
                continue;
            var gdop = MultilaterationSolver.ComputeGdop(trial, (r.Point.Value.X, r.Point.Value.Z));
            if (gdop < bestGdop)
            {
                bestGdop = gdop;
                bestX = cx;
                bestZ = cz;
            }
        }

        return (bestX, bestZ, bestGdop);
    }

    private static double ToleranceRadiusMetres(double gdop, MotherlodeGuidanceMode mode) =>
        mode == MotherlodeGuidanceMode.GenericSpread
            ? MaxToleranceMetres
            : Math.Clamp(MinToleranceMetres + (double.IsFinite(gdop) ? gdop * 2.5 : 12), MinToleranceMetres, MaxToleranceMetres);
}
