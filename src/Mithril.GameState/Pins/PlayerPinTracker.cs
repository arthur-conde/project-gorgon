using Mithril.GameState.Areas;
using Mithril.Shared.Diagnostics;
using Mithril.Shared.Logging;
using Microsoft.Extensions.Hosting;

namespace Mithril.GameState.Pins;

/// <summary>
/// Hosted-service implementation of <see cref="IPlayerPinTracker"/>. Tails
/// <see cref="IPlayerLogStream"/>, parses <c>ProcessMapPin{Add,Remove}</c> via
/// <see cref="MapPinParser"/>, and folds the stream into the current area's
/// pin set (#468).
///
/// <para><b>Lifecycle the service owns</b> (so no consumer hand-rolls it):</para>
/// <list type="bullet">
///   <item><b>Login / area-entry replay = idempotent upsert.</b> PG re-emits
///   the whole set as an <c>Add</c> burst on every entry with no preceding
///   clear. Pins are keyed by rounded coordinate, so a replayed pin at an
///   unchanged coordinate is a no-op and raises no <see cref="PinSetChange.Added"/>
///   — consumers stay quiet through the backlog.</item>
///   <item><b>No edit/move verb.</b> A rename/move is <c>Remove</c>+<c>Add</c>;
///   it surfaces as a <see cref="PinSetChange.Removed"/> then
///   <see cref="PinSetChange.Added"/>, which is faithful to the log.</item>
///   <item><b>Area transition = swap.</b> Pins are area-local; on any
///   <c>PlayerAreaTracker.CurrentArea</c> change the set is dropped and a
///   <see cref="PinSetChange.AreaChanged"/> (empty set) is raised. The
///   subsequent replay burst repopulates it for the new area.</item>
/// </list>
///
/// <para><b>Why a self-feeding BackgroundService.</b> Mirrors
/// <see cref="Movement.PlayerPositionTracker"/> — shared live game-state must
/// be available without any module being activated. It also calls
/// <see cref="PlayerAreaTracker.Observe(RawLogLine)"/> to keep the shared area
/// tracker warm (idempotent when the position tracker / Legolas also feed it).
/// No area reverse-scan seed is needed: the pin replay burst lands inside the
/// live window (after the login <c>ProcessAddPlayer</c> the window is seeded
/// to), so the set self-populates.</para>
///
/// <para><b>Threading.</b> Ingestion runs on the hosted-service loop thread;
/// <see cref="CurrentArea"/>/<see cref="CurrentAreaPins"/> reads, state
/// mutation and subscriber dispatch are serialised under <c>_lock</c>; each
/// notification carries an immutable snapshot, so handlers may hold it. They
/// run on the ingestion thread — non-trivial / UI work must marshal off.</para>
/// </summary>
public sealed class PlayerPinTracker : BackgroundService, IPlayerPinTracker
{
    private readonly IPlayerLogStream _stream;
    private readonly MapPinParser _parser;
    private readonly PlayerAreaTracker _areaTracker;
    private readonly IDiagnosticsSink? _diag;

    private readonly object _lock = new();
    private readonly List<Action<PinSetChanged>> _handlers = [];

    // Keyed by rounded coordinate: a pin's identity is its position (a rename
    // keeps the coordinate; a move is Remove+Add). Rounding only absorbs
    // float repr — PG re-replays byte-identical coordinate strings.
    private readonly Dictionary<PinKey, MapPin> _pins = new();
    private string? _trackedArea;
    private bool _areaInitialised;

    /// <param name="stream">The shared Player.log line stream this service
    /// tails (its live replay window already covers the login pin burst).</param>
    /// <param name="parser">Stateless line→<see cref="MapPinLogEvent"/> parser.</param>
    /// <param name="areaTracker">Shared area key source; also fed every line
    /// here so it stays warm without another module being active.</param>
    /// <param name="diag">Optional diagnostics sink (info on subscribe,
    /// warnings on ingestion / subscriber faults).</param>
    public PlayerPinTracker(
        IPlayerLogStream stream,
        MapPinParser parser,
        PlayerAreaTracker areaTracker,
        IDiagnosticsSink? diag = null)
    {
        _stream = stream;
        _parser = parser;
        _areaTracker = areaTracker;
        _diag = diag;
    }

    /// <inheritdoc/>
    public string? CurrentArea
    {
        get { lock (_lock) return _trackedArea; }
    }

    /// <inheritdoc/>
    public IReadOnlyList<MapPin> CurrentAreaPins
    {
        get { lock (_lock) return Snapshot(); }
    }

    /// <inheritdoc/>
    public IDisposable Subscribe(Action<PinSetChanged> handler)
    {
        ArgumentNullException.ThrowIfNull(handler);
        lock (_lock)
        {
            // Replay the current set before going live so a late subscriber
            // observes the same view already-attached handlers see. Mirrors
            // PlayerPositionTracker.Subscribe.
            Invoke(handler, new PinSetChanged(
                PinSetChange.Snapshot, _trackedArea, null, Snapshot(),
                DateTimeOffset.UtcNow));
            _handlers.Add(handler);
            return new Subscription(this, handler);
        }
    }

    /// <summary>
    /// The hosted-service ingestion loop: for every Player.log line, warm the
    /// shared area tracker, reconcile an area change, then fold a parsed
    /// pin event into the set. Exits when <paramref name="stoppingToken"/> is
    /// cancelled or the stream completes; per-line exceptions are logged and
    /// swallowed so one bad line never stops ingestion.
    /// </summary>
    /// <param name="stoppingToken">Host shutdown signal.</param>
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _diag?.Info("GameState.Pins", "Subscribing to Player.log for ProcessMapPin*");
        await foreach (var raw in _stream.SubscribeAsync(stoppingToken).ConfigureAwait(false))
        {
            try
            {
                // Same line stream carries area transitions; keep the shared
                // tracker warm even when no other feeder is active.
                _areaTracker.Observe(raw);
                ReconcileArea();

                if (_parser.TryParse(raw.Line, raw.Timestamp) is MapPinLogEvent evt)
                    Apply(evt);
            }
            catch (Exception ex)
            {
                _diag?.Warn("GameState.Pins", $"Ingestion error: {ex.Message}");
            }
        }
    }

    /// <summary>
    /// Drop the set whenever the shared area key changes (including the first
    /// time it becomes known). The set is area-local; the new area's replay
    /// burst — which follows the <c>LOADING LEVEL</c> line that moved the key
    /// — repopulates it.
    /// </summary>
    private void ReconcileArea()
    {
        var area = _areaTracker.CurrentArea;
        PinSetChanged? note = null;
        lock (_lock)
        {
            if (_areaInitialised && area == _trackedArea) return;
            _areaInitialised = true;
            _trackedArea = area;
            _pins.Clear();
            note = new PinSetChanged(
                PinSetChange.AreaChanged, area, null, Snapshot(),
                DateTimeOffset.UtcNow);
        }
        if (note is not null) Publish(note);
    }

    private void Apply(MapPinLogEvent evt)
    {
        var key = PinKey.From(evt.X, evt.Z);
        PinSetChanged? note = null;
        lock (_lock)
        {
            if (evt.Change == MapPinChange.Removed)
            {
                if (_pins.Remove(key, out var removed))
                    note = new PinSetChanged(
                        PinSetChange.Removed, _trackedArea, removed, Snapshot(),
                        ToOffset(evt.Timestamp));
            }
            else
            {
                var pin = new MapPin(
                    evt.X, evt.Z, evt.Label, evt.Shape, evt.Color, evt.RawList);
                // Idempotent: a replay-burst re-add of an unchanged pin is a
                // no-op and raises nothing. Only a new coordinate or a changed
                // label/appearance notifies.
                if (!_pins.TryGetValue(key, out var existing) || existing != pin)
                {
                    _pins[key] = pin;
                    note = new PinSetChanged(
                        PinSetChange.Added, _trackedArea, pin, Snapshot(),
                        ToOffset(evt.Timestamp));
                }
            }
        }
        if (note is not null) Publish(note);
    }

    /// <summary>Immutable copy of the current set. Caller holds <c>_lock</c>.</summary>
    private IReadOnlyList<MapPin> Snapshot() => _pins.Values.ToArray();

    private void Publish(PinSetChanged note)
    {
        Action<PinSetChanged>[] toFire;
        lock (_lock) toFire = _handlers.ToArray();
        foreach (var h in toFire) Invoke(h, note);
    }

    private void Invoke(Action<PinSetChanged> handler, PinSetChanged note)
    {
        try { handler(note); }
        catch (Exception ex) { _diag?.Warn("GameState.Pins", $"Subscriber threw: {ex.Message}"); }
    }

    /// <summary>
    /// Player.log timestamps are reconstructed as UTC <see cref="DateTime"/>s.
    /// Stamp the kind defensively so the offset is +00:00 rather than the
    /// host's local offset (mirrors PlayerPositionTracker.ToOffset).
    /// </summary>
    private static DateTimeOffset ToOffset(DateTime ts) =>
        new(DateTime.SpecifyKind(ts, DateTimeKind.Utc));

    private readonly record struct PinKey(long X100, long Z100)
    {
        public static PinKey From(double x, double z) => new(
            (long)Math.Round(x * 100.0, MidpointRounding.AwayFromZero),
            (long)Math.Round(z * 100.0, MidpointRounding.AwayFromZero));
    }

    private sealed class Subscription : IDisposable
    {
        private PlayerPinTracker? _owner;
        private readonly Action<PinSetChanged> _handler;

        public Subscription(PlayerPinTracker owner, Action<PinSetChanged> handler)
        {
            _owner = owner;
            _handler = handler;
        }

        public void Dispose()
        {
            var owner = Interlocked.Exchange(ref _owner, null);
            if (owner is null) return;
            lock (owner._lock) { owner._handlers.Remove(_handler); }
        }
    }
}
