using Legolas.Domain;
using Legolas.Flow;
using Mithril.GameState.Movement;
using Mithril.GameState.Pins;
using Mithril.Shared.Diagnostics;

namespace Legolas.Services;

/// <summary>
/// Immutable view of the in-flight Motherlode solve for the VM. <see cref="Surveys"/>
/// and <see cref="Locations"/> are snapshots (safe to bind/iterate off-thread);
/// <see cref="Guidance"/> is the single most relevant actionable hint (GDOP /
/// missing-fix), or null when nothing needs saying.
/// </summary>
/// <param name="Locations">The shared ordered Pᵢ set — one entry per spot the
/// player measured at, in click order. Surfaced (not just counted) so the UI
/// can phrase a solved treasure relative to "your spot #N" (#113 Layer 1, the
/// zero-standoff reference tier). Includes fix-less rows (Confidence 0) so a
/// row index lines up with <see cref="MotherlodeSurvey.DistancesByLocation"/>.</param>
public sealed record MotherlodeStatus(
    int LocationCount,
    int LocationsWithFix,
    WorldCoord? LastPlayerWorld,
    IReadOnlyList<MotherlodeSurvey> Surveys,
    IReadOnlyList<MotherlodePositionSample> Locations,
    string? Guidance);

/// <summary>
/// Owns the rebuilt Motherlode mechanic (#488): correlates the
/// <see cref="MotherlodeUseDetected"/> use gesture, a position feeder fix
/// (#1 opportunistic log position / #2 map pin — source-agnostic), and the
/// following ChatLog <see cref="MotherlodeDistance"/> line(s) into a shared
/// location-sample set, then runs <see cref="IMultilaterationSolver"/> per
/// motherlode slot.
///
/// <para><b>Pairing is label-agnostic and temporal</b> (hard rule, mirrors
/// #454): a use opens/extends a location; the feeder fix nearest in time to
/// the use (within <see cref="MaxFeederGapSeconds"/>) is that location's Pᵢ;
/// distance lines arriving within <see cref="DistanceWindowSeconds"/> of the
/// last use bind in arrival order — the k-th to slot k (the batching contract:
/// a player carries an inventory of maps and clicks them in a stable order at
/// every spot; the log carries no per-target identity). The ingestion services
/// already gate Motherlode mode and the session-replay backlog
/// (<c>_liveSince</c>), so a finished run can't repopulate here.</para>
/// </summary>
public sealed class MotherlodeMeasurementCoordinator : IDisposable
{
    // ---- #488 "decide during build" knobs (conservative; tune empirically) --

    // A feeder fix older than this vs the use is too stale to be "where I am
    // now". Comfortably exceeds the ~15–30 s relog load of the max-accuracy
    // ProcessAddPlayer procedure.
    private const double MaxFeederGapSeconds = 120;

    // Uses within this window of the previous use are the same physical spot
    // (the player clicking several carried maps); a later use starts a new
    // location.
    private const double LocationClusterSeconds = 30;

    // A distance line must follow its use within this window to bind.
    private const double DistanceWindowSeconds = 20;

    // Inverse-variance-ish confidence per feeder (→ solver weight). Accuracy
    // ordering is load-bearing (#488 table); the exact values are an open knob.
    private static double Confidence(MotherlodePositionSource src, PlayerPositionSource? pos) => src switch
    {
        MotherlodePositionSource.LogPosition => pos == PlayerPositionSource.Spawn ? 1.0 : 0.8,
        MotherlodePositionSource.MapPin => 0.6,
        MotherlodePositionSource.Gazetteer => 0.3,
        MotherlodePositionSource.OverlayClick => 0.15,
        _ => 0.5,
    };

    private readonly IMultilaterationSolver _solver;
    private readonly MotherlodeFlowController _flow;
    private readonly IDiagnosticsSink? _diag;
    private readonly IDisposable? _posSub;
    private readonly IDisposable? _pinSub;

    private readonly object _gate = new();
    private readonly MotherlodeSession _session = new();

    // Latest feeder fix seen from a tracker (cross-thread; value-only).
    private (WorldCoord World, MotherlodePositionSource Src, PlayerPositionSource? Pos, DateTimeOffset At)? _latestFix;

    // The location currently accreting uses/distances, or null.
    private sealed class OpenLocation
    {
        public MotherlodePositionSample? Sample;
        public DateTimeOffset LastUseAt;
        public int RowIndex = -1;            // index into _session.LocationSamples once committed
        public int DistanceCount;            // distances bound so far (→ slot index)
    }
    private OpenLocation? _open;
    private string? _guidance;

    public event Action? Changed;

    public MotherlodeMeasurementCoordinator(
        IMultilaterationSolver solver,
        MotherlodeFlowController flow,
        IPlayerPositionTracker positionTracker,
        IPlayerPinTracker pinTracker,
        IDiagnosticsSink? diag = null)
    {
        _solver = solver;
        _flow = flow;
        _diag = diag;
        // Replay-on-subscribe is fine: a fix only matters once a use references
        // it within MaxFeederGap, so a stale replayed fix self-expires.
        _posSub = positionTracker.Subscribe(OnPlayerPosition);
        _pinSub = pinTracker.Subscribe(OnPinChanged);
    }

    // ---- Feeder inputs (tracker thread) ----------------------------------

    private void OnPlayerPosition(PlayerPosition p)
    {
        lock (_gate)
            _latestFix = (new WorldCoord(p.X, p.Y, p.Z),
                MotherlodePositionSource.LogPosition, p.Source, p.MeasuredAt);
    }

    private void OnPinChanged(PinSetChanged c)
    {
        // Only a genuinely new pin is a feeder; Snapshot/AreaChanged replays
        // carry no single Pin and must not move the fix.
        if (c.Kind != PinSetChange.Added || c.Pin is not { } pin) return;
        lock (_gate)
            _latestFix = (new WorldCoord(pin.X, 0, pin.Z),
                MotherlodePositionSource.MapPin, null, c.ObservedAt);
    }

    // ---- Use / distance / collection (UI thread, via ingestion PostToUi) --

    /// <summary>The player clicked a Motherlode map. Opens or extends the
    /// current location and binds the feeder fix nearest in time.</summary>
    public void OnUse(DateTimeOffset at)
    {
        bool changed;
        lock (_gate)
        {
            if (_open is { } o && (at - o.LastUseAt).TotalSeconds > LocationClusterSeconds)
                _open = null;                       // player moved on — new spot

            if (_open is null)
            {
                _open = new OpenLocation { LastUseAt = at };
                if (NearestFix(at) is { } f)
                {
                    _open.Sample = new MotherlodePositionSample(
                        f.World, f.Src, Confidence(f.Src, f.Pos), f.At);
                    _guidance = null;
                }
                else
                {
                    _guidance = "No recent position fix for that reading — relog " +
                                "at the spot (best) or drop a map pin before using.";
                    _diag?.Info("Legolas.Motherlode", "Use with no feeder fix in window.");
                }
            }
            _open.LastUseAt = at;
            changed = true;
        }
        _flow.NoteMeasurement("motherlode-use");
        if (changed) Raise();
    }

    /// <summary>A ChatLog distance reading. Binds to the open location's next
    /// slot (arrival order = slot index) and re-solves that slot.</summary>
    public void OnDistance(int metres, DateTimeOffset at)
    {
        lock (_gate)
        {
            if (_open is not { } o)
            {
                _diag?.Trace("Legolas.Motherlode", $"Distance {metres}m with no open location — dropped.");
                return;
            }
            if ((at - o.LastUseAt).TotalSeconds > DistanceWindowSeconds)
            {
                _diag?.Trace("Legolas.Motherlode", $"Distance {metres}m outside use window — dropped.");
                return;
            }

            EnsureCommitted(o);
            var slot = o.DistanceCount;
            o.DistanceCount++;
            SetDistance(o.RowIndex, slot, metres);
            ResolveSlot(slot);
        }
        _flow.NoteMeasurement("motherlode-distance");
        Raise();
    }

    /// <summary>Collection-gated progression: a "<c>… Metal Slab</c>" entering
    /// inventory marks the next uncollected motherlode collected.</summary>
    public void OnItemCollected(string name)
    {
        if (name.IndexOf("Metal Slab", StringComparison.OrdinalIgnoreCase) < 0) return;
        bool changed = false;
        lock (_gate)
        {
            MotherlodeSurvey? best = null;
            var bestOrder = int.MaxValue;
            for (var i = 0; i < _session.Surveys.Count; i++)
            {
                var s = _session.Surveys[i];
                if (s.Collected) continue;
                var ord = s.RouteOrder ?? i;
                if (best is null || ord < bestOrder) { best = s; bestOrder = ord; }
            }
            if (best is { } b)
            {
                var idx = _session.Surveys.IndexOf(b);
                _session.Surveys[idx] = b with { Collected = true };
                changed = true;
            }
        }
        if (changed) Raise();
    }

    public void Reset()
    {
        lock (_gate)
        {
            _session.LocationSamples.Clear();
            _session.Surveys.Clear();
            _open = null;
            _latestFix = null;
            _guidance = null;
        }
        _flow.Reset();
        Raise();
    }

    public MotherlodeStatus Snapshot()
    {
        lock (_gate)
        {
            var withFix = _session.LocationSamples.Count(s => s.Confidence > 0);
            WorldCoord? last = null;
            for (var i = _session.LocationSamples.Count - 1; i >= 0; i--)
                if (_session.LocationSamples[i].Confidence > 0)
                { last = _session.LocationSamples[i].World; break; }
            return new MotherlodeStatus(
                _session.LocationSamples.Count,
                withFix,
                last,
                _session.Surveys.ToArray(),
                _session.LocationSamples.ToArray(),
                _guidance);
        }
    }

    /// <summary>Persist the optimized visit order computed by the VM (route
    /// optimization is calibration-free — world (X,Z) ordering is invariant
    /// under the display similarity transform).</summary>
    public void ApplyRouteOrder(IReadOnlyList<Guid> orderedIds)
    {
        lock (_gate)
        {
            for (var i = 0; i < _session.Surveys.Count; i++)
            {
                var s = _session.Surveys[i];
                var pos = IndexOf(orderedIds, s.Id);
                _session.Surveys[i] = s with { RouteOrder = pos >= 0 ? pos : null };
            }
        }
        _flow.OptimizeRoute();
        Raise();

        static int IndexOf(IReadOnlyList<Guid> ids, Guid id)
        {
            for (var i = 0; i < ids.Count; i++) if (ids[i] == id) return i;
            return -1;
        }
    }

    // ---- internals (all under _gate) -------------------------------------

    private (WorldCoord World, MotherlodePositionSource Src, PlayerPositionSource? Pos, DateTimeOffset At)? NearestFix(DateTimeOffset useAt)
    {
        if (_latestFix is not { } f) return null;
        return Math.Abs((useAt - f.At).TotalSeconds) <= MaxFeederGapSeconds ? f : null;
    }

    private void EnsureCommitted(OpenLocation o)
    {
        if (o.RowIndex >= 0) return;
        o.RowIndex = _session.LocationSamples.Count;
        // A location with no fix still occupies a row so slot/distance indices
        // stay aligned across locations; Confidence 0 ⇒ excluded from solves.
        _session.LocationSamples.Add(o.Sample
            ?? new MotherlodePositionSample(default, MotherlodePositionSource.LogPosition, 0, o.LastUseAt));
    }

    private void SetDistance(int row, int slot, int metres)
    {
        while (_session.Surveys.Count <= slot)
            _session.Surveys.Add(MotherlodeSurvey.Create());

        var s = _session.Surveys[slot];
        var d = s.DistancesByLocation.ToList();
        while (d.Count <= row) d.Add(0);     // 0 = "no reading for this slot here"
        d[row] = metres;
        _session.Surveys[slot] = s with { DistancesByLocation = d };
    }

    private void ResolveSlot(int slot)
    {
        if (slot < 0 || slot >= _session.Surveys.Count) return;
        var s = _session.Surveys[slot];

        var samples = new List<MultilaterationSample>();
        for (var row = 0; row < _session.LocationSamples.Count && row < s.DistancesByLocation.Count; row++)
        {
            var loc = _session.LocationSamples[row];
            var dist = s.DistancesByLocation[row];
            if (loc.Confidence <= 0 || dist <= 0) continue;   // skip fix-less / unread
            samples.Add(new MultilaterationSample(loc.World.X, loc.World.Z, dist, loc.Confidence));
        }

        if (samples.Count < 3)
        {
            _session.Surveys[slot] = s with { SolvedWorld = null, Gdop = null, ResidualRms = null };
            return;
        }

        var r = _solver.Solve(samples);
        _session.Surveys[slot] = s with
        {
            SolvedWorld = r.Point,
            Gdop = double.IsFinite(r.Gdop) ? r.Gdop : null,
            ResidualRms = double.IsFinite(r.ResidualRms) ? r.ResidualRms : null,
        };
        if (r.Guidance is { } g) _guidance = g;
        else if (r.Quality == MultilaterationQuality.Solved) _guidance = null;
    }

    private void Raise() => Changed?.Invoke();

    public void Dispose()
    {
        _posSub?.Dispose();
        _pinSub?.Dispose();
    }
}
