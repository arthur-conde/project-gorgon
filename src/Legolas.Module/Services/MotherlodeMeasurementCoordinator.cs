using Legolas.Domain;
using Legolas.Flow;
using Mithril.GameState.Areas;
using Mithril.GameState.Inventory;
using Mithril.GameState.Movement;
using Mithril.GameState.Pins;
using Mithril.Shared.Diagnostics;
using Mithril.Shared.Reference;

namespace Legolas.Services;

/// <summary>
/// Immutable view of the in-flight Motherlode solve for the VM. <see cref="Surveys"/>
/// and <see cref="Locations"/> are snapshots (safe to bind/iterate off-thread);
/// <see cref="Guidance"/> is the single most relevant actionable hint.
/// </summary>
/// <param name="Locations">The shared ordered Pᵢ set — one entry per spot the
/// player measured at. Includes fix-less rows (Confidence 0) so a row index
/// lines up with <see cref="MotherlodeSurvey.DistancesByLocation"/>.</param>
/// <param name="ReadsPerLocation">Per-spot count of bound distance readings
/// (parallel to <see cref="Locations"/>) — the passive multi-map shape
/// surface ("Spot 1: 5 · Spot 2: 4").</param>
/// <param name="MapsDug">Count of motherlode maps consumed this session (each
/// <c>ProcessDeleteItem</c> of a motherlode map = one treasure found). The
/// session summary is this + elapsed time; loot is a documented data ceiling
/// (the dig spawns a mining node — yield is decoupled, no log link).</param>
public sealed record MotherlodeStatus(
    int LocationCount,
    int LocationsWithFix,
    WorldCoord? LastPlayerWorld,
    IReadOnlyList<MotherlodeSurvey> Surveys,
    IReadOnlyList<MotherlodePositionSample> Locations,
    string? Guidance,
    IReadOnlyList<int> ReadsPerLocation,
    int MapsDug);

/// <summary>
/// Owns the rebuilt Motherlode mechanic (#488). Confirmed empirically from the
/// live Player.log: <b>one map = one treasure</b>; the read line carries no
/// per-map identity (type name only) and the inventory slot ≠ the grid the
/// player arranges by, so read→treasure binding cannot be derived from the log.
///
/// <para><b>Create-on-use, not on stock.</b> A working slot is created lazily
/// by a <i>reading</i>, never by holding inventory — so carrying 100+ maps
/// costs nothing until measured. The map <i>type</i> name from the
/// <c>ProcessDoDelayLoop</c> use line is a display label only.</para>
///
/// <para><b>Multi-map (default, toggle) vs serial.</b> With the
/// <see cref="LegolasSettings.MotherlodeMultiMapMode"/> toggle on (other tools'
/// posture, and the evidenced primary workflow — a stack read in a fixed order,
/// the same set re-read at each spot), the k-th read at the defining (first)
/// spot creates working slot k; later spots re-read that working set in order
/// (the Survey/Treasure-Cartography assumptions: same order every spot, maps
/// arranged sequentially). Off ⇒ serial single-active-treasure. The contract is
/// validated against cross-spot read counts (<see cref="DetectBatchDivergence"/>),
/// never silently authoritative.</para>
///
/// <para><b>Completion.</b> A motherlode-map <c>Deleted</c> (the dig; it spawns
/// a mining node, loot decoupled) increments <see cref="MotherlodeStatus.MapsDug"/>
/// and retires the next uncollected slot. Position is temporally paired from
/// the highest-confidence feeder; the <c>@me</c>/character-named pin (#497) is
/// the intended high-accuracy per-read source.</para>
/// </summary>
public sealed class MotherlodeMeasurementCoordinator : IDisposable
{
    // ---- #488 knobs (conservative; tune empirically) ---------------------

    // A feeder fix older than this vs the use is too stale to be "where I am
    // now". Comfortably exceeds the ~15–30 s relog of the max-accuracy
    // ProcessAddPlayer procedure and the @me-pin→read latency.
    private const double MaxFeederGapSeconds = 120;

    // Uses within this window of the previous use are the same physical spot.
    private const double LocationClusterSeconds = 30;

    // A distance line must follow its use within this window to bind.
    private const double DistanceWindowSeconds = 20;

    // Risk-1: when the time gate trips but the feeder fix is within this many
    // metres of the open location's *committed first* anchor, the player is
    // still standing there (a slow re-read), not at a new spot — keep the
    // location so a >30 s same-spot pause can't fork a phantom row / desync.
    // 12 m covers MapPin↔LogPosition feeder standoff while staying >2× below
    // the solver's ~30 m distinct-spot guidance.
    private const double SameSpotRadiusMetres = 12;

    // A ProcessDeleteItem lands ~1 s after the completing use; far tighter
    // than the distance window so an unrelated deletion can't be mistaken for
    // a completion.
    private const double MapConsumeWindowSeconds = 5;

    // Inverse-variance-ish confidence per feeder (→ solver weight). Accuracy
    // ordering is load-bearing (#488 table); exact values are an open knob.
    // The @me/character-named pin (#497) is the player's own location (the
    // intended high-accuracy per-read path), so it ranks well above a generic
    // hand-placed map pin.
    private static double Confidence(MotherlodePositionSource src, PlayerPositionSource? pos) => src switch
    {
        MotherlodePositionSource.LogPosition => pos == PlayerPositionSource.Spawn ? 1.0 : 0.8,
        MotherlodePositionSource.NamedMapPin => 0.85,
        MotherlodePositionSource.MapPin => 0.6,
        MotherlodePositionSource.Gazetteer => 0.3,
        MotherlodePositionSource.OverlayClick => 0.15,
        _ => 0.5,
    };

    private readonly IMultilaterationSolver _solver;
    private readonly MotherlodeFlowController _flow;
    private readonly ICharacterPinAnchor? _characterPin;
    private readonly PlayerAreaTracker? _areaTracker;
    private readonly IDiagnosticsSink? _diag;
    private readonly IReferenceDataService? _refData;
    private readonly LegolasSettings? _settings;
    private readonly IDisposable? _posSub;
    private readonly IDisposable? _pinSub;
    private readonly IDisposable? _invSub;

    // The area the in-flight measurement is anchored in. Multilateration is in
    // the area-local engine frame and maps are area-specific, so crossing
    // areas invalidates every position fix / slot — the measurement is cleared
    // (the session-cumulative dug count is kept; it isn't frame-bound).
    private string? _sessionArea;

    private readonly object _gate = new();
    private readonly MotherlodeSession _session = new();

    // Maps consumed this session (each motherlode-map Deleted = a dig). The
    // summary is this + elapsed time; no per-completed entity is retained.
    private int _dugMaps;

    // The map-type label from the most recent use, applied to a slot the next
    // read creates (display only — identical across a same-type stack).
    private string? _pendingUseName;

    // Latest feeder fix seen from a tracker (cross-thread; value-only).
    private (WorldCoord World, MotherlodePositionSource Src, PlayerPositionSource? Pos, DateTimeOffset At)? _latestFix;

    // The location currently accreting uses/distances, or null.
    private sealed class OpenLocation
    {
        public MotherlodePositionSample? Sample;
        public DateTimeOffset LastUseAt;
        public int RowIndex = -1;            // index into _session.LocationSamples once committed
        public int DistanceCount;            // distances bound at this spot so far (per-spot ordinal)
    }
    private OpenLocation? _open;
    private DateTimeOffset? _lastUseAt;
    private string? _guidance;

    public event Action? Changed;

    public MotherlodeMeasurementCoordinator(
        IMultilaterationSolver solver,
        MotherlodeFlowController flow,
        IPlayerPositionTracker positionTracker,
        IPlayerPinTracker pinTracker,
        IInventoryService? inventory = null,
        IReferenceDataService? refData = null,
        LegolasSettings? settings = null,
        ICharacterPinAnchor? characterPin = null,
        PlayerAreaTracker? areaTracker = null,
        IDiagnosticsSink? diag = null)
    {
        _solver = solver;
        _flow = flow;
        _characterPin = characterPin;
        _areaTracker = areaTracker;
        _refData = refData;
        _settings = settings;
        _diag = diag;
        // Replay-on-subscribe is fine: a position fix only matters once a use
        // references it within MaxFeederGap. Inventory is consumed for the
        // *Deleted* completion signal only — never to create slots from stock.
        _posSub = positionTracker.Subscribe(OnPlayerPosition);
        _pinSub = pinTracker.Subscribe(OnPinChanged);
        _invSub = inventory?.Subscribe(OnInventory);
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
        // carry no single Pin and must not move the fix. The @me /
        // character-named pin dropped at the read spot is the intended
        // high-accuracy position source.
        if (c.Kind != PinSetChange.Added || c.Pin is not { } pin) return;
        // #497: a pin the player named after their character / "@me" is an
        // explicit "I am here" — prefer it over an arbitrary waypoint.
        var src = _characterPin?.IsSelfPin(pin) == true
            ? MotherlodePositionSource.NamedMapPin
            : MotherlodePositionSource.MapPin;
        lock (_gate)
            _latestFix = (new WorldCoord(pin.X, 0, pin.Z), src, null, c.ObservedAt);
    }

    // ---- Completion (inventory thread) -----------------------------------

    /// <summary>
    /// A motherlode-map <c>Deleted</c> is the dig: it consumed the map (and
    /// spawned a mining node — loot is decoupled). Count it and retire the next
    /// uncollected slot. The <c>Added</c> branch is intentionally absent —
    /// holding stock must create nothing (the 100-maps pathology).
    /// </summary>
    private void OnInventory(InventoryEvent e)
    {
        if (e.Kind != InventoryEventKind.Deleted || !IsMotherlodeMap(e.InternalName)) return;
        bool changed = false;
        lock (_gate)
        {
            _dugMaps++;

            // If this delete is the completing use (a use that opened a
            // location which never received a distance, within the tight
            // window), discard that ephemeral location so it doesn't pollute
            // the solve / trip divergence.
            var at = new DateTimeOffset(DateTime.SpecifyKind(e.Timestamp, DateTimeKind.Utc));
            if (_open is { } o && o.DistanceCount == 0
                && _lastUseAt is { } lu
                && (at - lu).TotalSeconds <= MapConsumeWindowSeconds)
            {
                DiscardOpenLocation(o);
                _open = null;
            }

            // Retire the next uncollected slot (lowest route order, else
            // creation order) — order-based, mirrors the dig-the-cluster flow.
            MotherlodeSurvey? best = null;
            var bestOrder = int.MaxValue;
            var bestIdx = -1;
            for (var i = 0; i < _session.Surveys.Count; i++)
            {
                var s = _session.Surveys[i];
                if (s.Collected) continue;
                var ord = s.RouteOrder ?? i;
                if (best is null || ord < bestOrder) { best = s; bestOrder = ord; bestIdx = i; }
            }
            if (best is { } b)
            {
                _session.Surveys[bestIdx] = b with { Collected = true };
                _diag?.Info("Legolas.Motherlode",
                    $"Map consumed ({e.InternalName}) — slot {bestIdx} collected; dug={_dugMaps}.");
            }
            else
            {
                _diag?.Info("Legolas.Motherlode",
                    $"Map consumed ({e.InternalName}) — dug={_dugMaps} (no measured slot to retire).");
            }
            changed = true;
        }
        if (changed) Raise();
    }

    private bool IsMotherlodeMap(string internalName) =>
        _refData is { } rd
        && rd.ItemsByInternalName.TryGetValue(internalName, out var item)
        && item.Name is { } n
        && n.EndsWith("Motherlode Map", StringComparison.OrdinalIgnoreCase);

    // A committed-but-distance-less location was a completing gesture, not a
    // measurement: roll its row out of the shared sample set so it weighs on
    // no solve and counts toward no divergence check.
    private void DiscardOpenLocation(OpenLocation o)
    {
        if (o.RowIndex < 0 || o.RowIndex != _session.LocationSamples.Count - 1) return;
        _session.LocationSamples.RemoveAt(o.RowIndex);
        for (var i = 0; i < _session.Surveys.Count; i++)
        {
            var s = _session.Surveys[i];
            if (s.DistancesByLocation.Count <= o.RowIndex) continue;
            var d = s.DistancesByLocation.ToList();
            d.RemoveAt(o.RowIndex);
            _session.Surveys[i] = s with { DistancesByLocation = d };
        }
    }

    // ---- Use / distance (UI thread, via ingestion PostToUi) --------------

    /// <summary>The player clicked a Motherlode map. Opens or extends the
    /// current location; binds the feeder fix nearest in time. Risk-1: a use
    /// after the time gate but still within <see cref="SameSpotRadiusMetres"/>
    /// of the frozen anchor keeps the same location (slow same-spot re-read).
    /// <paramref name="mapName"/> is the use-line map <i>type</i> label applied
    /// to a slot the next read creates (display only).</summary>
    public void OnUse(DateTimeOffset at, string? mapName = null)
    {
        bool changed;
        lock (_gate)
        {
            // Area scoping: multilateration is area-local-frame and maps are
            // area-specific. A confirmed area change invalidates every in-flight
            // fix/slot — clear the measurement (keep the session dug count).
            // Null = area unknown (char-select / pre-transition) → don't reset;
            // self-heals on the next confirmed area.
            var cur = _areaTracker?.CurrentArea;
            if (cur is not null)
            {
                if (_sessionArea is not null && cur != _sessionArea)
                {
                    _diag?.Info("Legolas.Motherlode",
                        $"Area {_sessionArea} → {cur}: clearing in-flight measurement (dug count kept).");
                    ClearMeasurement();
                }
                _sessionArea = cur;
            }

            _pendingUseName = mapName;

            if (_open is { } o && (at - o.LastUseAt).TotalSeconds > LocationClusterSeconds)
            {
                // Position-primary: compare the *current* fix to the committed
                // first anchor only — never chain-compare to the previous fix,
                // never refresh o.Sample (that would drift-walk the anchor).
                var nf = NearestFix(at);
                var sameSpot = o.Sample is { } anchor && anchor.Confidence > 0
                    && nf is { } f
                    && PlanarDist(anchor.World, f.World) <= SameSpotRadiusMetres;
                if (!sameSpot) _open = null;     // genuine move, or no fix to compare
            }

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
                    _guidance = "No position for that reading — drop an @me pin " +
                                "at the spot (best) before using, or relog.";
                    _diag?.Info("Legolas.Motherlode", "Use with no feeder fix in window.");
                }
            }
            _open.LastUseAt = at;
            _lastUseAt = at;
            changed = true;
        }
        _flow.NoteMeasurement("motherlode-use");
        if (changed) Raise();
    }

    /// <summary>A ChatLog distance reading. Creates/binds the slot the current
    /// mode + per-spot ordinal selects, then re-solves it.</summary>
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
            var slot = SelectSlot(o);
            if (slot < 0)
            {
                _diag?.Trace("Legolas.Motherlode", $"Distance {metres}m — no slot to bind — dropped.");
                return;
            }
            o.DistanceCount++;
            SetDistance(o.RowIndex, slot, metres);
            ResolveSlot(slot);
            if (DetectBatchDivergence() is { } div) _guidance = div;
        }
        _flow.NoteMeasurement("motherlode-distance");
        Raise();
    }

    /// <summary>
    /// Which slot the k-th reading at this spot binds to. Multi-map (default):
    /// the defining (first) spot creates slot k as the stack is read; later
    /// spots re-read the same working set in order (divergence flags a
    /// shortfall). Serial / toggle off: the single active uncollected treasure.
    /// </summary>
    private int SelectSlot(OpenLocation o)
    {
        var k = o.DistanceCount;                       // 0-based per-spot ordinal
        var multiMap = _settings?.MotherlodeMultiMapMode ?? true;
        var count = _session.Surveys.Count;

        if (!multiMap)
        {
            // Serial: the single active treasure = lowest-index uncollected
            // slot; if none (all dug / none yet), a new one is created.
            for (var i = 0; i < count; i++)
                if (!_session.Surveys[i].Collected) return i;
            return count;                              // SetDistance grows one
        }

        // Multi-map declared-order. Defining pass (first measured spot) grows
        // the working set lazily; later spots re-read it in the same order,
        // clamped so an over/under-read can't throw — divergence surfaces it.
        var definingPass = o.RowIndex == 0;
        if (definingPass) return k;
        if (count == 0) return 0;
        return Math.Min(k, count - 1);
    }

    // Clears the in-flight measurement (area-local frames) — NOT the
    // session-cumulative dug count, which isn't frame-bound. Caller holds _gate.
    private void ClearMeasurement()
    {
        _session.LocationSamples.Clear();
        _session.Surveys.Clear();
        _open = null;
        _lastUseAt = null;
        _latestFix = null;
        _guidance = null;
        _pendingUseName = null;
    }

    public void Reset()
    {
        lock (_gate)
        {
            ClearMeasurement();
            _dugMaps = 0;
            _sessionArea = null;
        }
        _flow.Reset();
        Raise();
    }

    public MotherlodeStatus Snapshot()
    {
        lock (_gate)
        {
            var rows = _session.LocationSamples.Count;
            var withFix = _session.LocationSamples.Count(s => s.Confidence > 0);
            WorldCoord? last = null;
            for (var i = rows - 1; i >= 0; i--)
                if (_session.LocationSamples[i].Confidence > 0)
                { last = _session.LocationSamples[i].World; break; }

            var reads = ReadsPerLocation(rows);
            return new MotherlodeStatus(
                rows,
                withFix,
                last,
                _session.Surveys.ToArray(),
                _session.LocationSamples.ToArray(),
                _guidance,
                reads,
                _dugMaps);
        }
    }

    /// <summary>Persist the optimized visit order computed by the VM.</summary>
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

    private static double PlanarDist(WorldCoord a, WorldCoord b)
    {
        var dx = a.X - b.X;
        var dz = a.Z - b.Z;
        return Math.Sqrt(dx * dx + dz * dz);
    }

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
            // Create-on-read: a new working slot, labelled with the type name
            // from the use that produced this read (display only).
            _session.Surveys.Add(MotherlodeSurvey.Create() with { MapName = _pendingUseName });

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
            _session.Surveys[slot] = s with
            {
                SolvedWorld = null, Gdop = null, ResidualRms = null,
                Quality = MultilaterationQuality.Insufficient,
            };
            return;
        }

        var r = _solver.Solve(samples);
        _session.Surveys[slot] = s with
        {
            SolvedWorld = r.Point,
            Gdop = double.IsFinite(r.Gdop) ? r.Gdop : null,
            ResidualRms = double.IsFinite(r.ResidualRms) ? r.ResidualRms : null,
            Quality = r.Quality,
        };
        if (r.Guidance is { } g) _guidance = g;
        else if (r.Quality == MultilaterationQuality.Solved) _guidance = null;
    }

    private int[] ReadsPerLocation(int rows)
    {
        var reads = new int[rows];
        foreach (var s in _session.Surveys)
            for (var r = 0; r < rows && r < s.DistancesByLocation.Count; r++)
                if (s.DistancesByLocation[r] > 0) reads[r]++;
        return reads;
    }

    /// <summary>
    /// Validate the declared read-order contract by cross-spot consistency: a
    /// committed, closed (non-open) spot that read fewer maps than a later
    /// closed spot is very likely misaligned. Excludes the still-open final
    /// batch (it legitimately trails). Working-set based — never keyed off
    /// inventory-held stock. Returns the actionable message for the first
    /// offending spot, else null.
    /// </summary>
    private string? DetectBatchDivergence()
    {
        var rows = _session.LocationSamples.Count;
        if (rows == 0) return null;
        var reads = ReadsPerLocation(rows);
        var openRow = _open?.RowIndex ?? -1;

        var maxClosed = 0;
        for (var r = 0; r < rows; r++)
            if (r != openRow) maxClosed = Math.Max(maxClosed, reads[r]);

        for (var r = 0; r < rows; r++)
        {
            if (r == openRow) continue;
            if (reads[r] == 0) continue;            // not yet read here at all
            if (reads[r] < maxClosed)
                return $"Spot #{r + 1} read fewer maps than a later spot — readings " +
                       "may be misaligned; re-read that spot in the same order, or Reset.";
        }
        return null;
    }

    private void Raise() => Changed?.Invoke();

    public void Dispose()
    {
        _posSub?.Dispose();
        _pinSub?.Dispose();
        _invSub?.Dispose();
    }
}
