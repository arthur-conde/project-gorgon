using Mithril.GameState.Areas.Parsing;
using Mithril.Shared.Diagnostics;
using Mithril.Shared.Logging;
using Microsoft.Extensions.Hosting;

namespace Mithril.GameState.Pins;

/// <summary>
/// Hosted-service implementation of <see cref="IPlayerPinTracker"/>.
/// Subscribes to L1's unified classified pipe (#556 Phase 3), parses
/// <c>ProcessMapPin{Add,Remove}</c> on the <see cref="LocalPlayerLogLine"/>
/// payload via <see cref="MapPinParser"/>, and folds the stream into the
/// current area's pin set (#468).
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
///   <item><b>Area transition = swap.</b> Pins are area-local; the tracker
///   parses <see cref="SystemSignalKind.AreaLoading"/> envelopes off the
///   unified pipe and clears the set on any change, raising a
///   <see cref="PinSetChange.AreaChanged"/> (empty set). The subsequent
///   replay burst repopulates it for the new area.</item>
/// </list>
///
/// <para><b>Why the unified pipe.</b> Pre-#556 this tracker read
/// <c>PlayerAreaTracker.CurrentArea</c> for the area-reconcile and tailed
/// the raw Player.log for pin events — a two-pump arrangement that risked
/// reading a stale area at the moment a pin-burst arrived. Subscribing to
/// the L0.5 unified classified pipe gives one ordered stream of
/// <c>AreaLoading</c> and <c>LocalPlayer</c> envelopes through a single
/// subscription, so the area reconcile always precedes the pins for that
/// area. Combat envelopes silently no-op.</para>
///
/// <para><b>Threading.</b> The L1 driver's <c>InlineBridge</c> invokes the
/// handler on its pump thread; <see cref="CurrentArea"/>/<see cref="CurrentAreaPins"/>
/// reads, state mutation, and subscriber dispatch are serialised under
/// <c>_lock</c>; each notification carries an immutable snapshot, so
/// handlers may hold it. They run on the L1 pump thread — non-trivial / UI
/// work must marshal off.</para>
/// </summary>
public sealed class PlayerPinTracker : BackgroundService, IPlayerPinTracker
{
    private readonly ILogStreamDriver _driver;
    private readonly MapPinParser _parser;
    private readonly AreaTransitionParser _areaParser;
    private readonly IDiagnosticsSink? _diag;

    private readonly object _lock = new();
    private readonly List<Action<PinSetChanged>> _handlers = [];

    // Keyed by rounded coordinate: a pin's identity is its position (a rename
    // keeps the coordinate; a move is Remove+Add). Rounding only absorbs
    // float repr — PG re-replays byte-identical coordinate strings.
    private readonly Dictionary<PinKey, MapPin> _pins = new();
    private string? _trackedArea;
    private bool _areaInitialised;
    private ILogSubscription? _subscription;

    // Most-recent envelope timestamp the tracker has applied (area
    // transition or pin add/remove). Subscribe-replay stamps its synthetic
    // Snapshot notification with this so late subscribers observe the same
    // envelope-time view already-attached handlers were given (principle 13:
    // never leak wall clock into world-event-driven state). Default
    // (DateTimeOffset.MinValue) when no envelope has been applied yet —
    // the snapshot payload is also empty in that case, so the stamp is
    // never meaningful in isolation.
    private DateTimeOffset _lastObservedAt;

    private const string DiagCategory = "GameState.Pins";

    /// <param name="driver">L1 driver — subscribed to via the
    /// <see cref="IClassifiedPlayerLogLine"/> unified pipe.</param>
    /// <param name="parser">Stateless line→<see cref="MapPinLogEvent"/> parser.</param>
    /// <param name="areaParser">Stateless parser used to extract the area
    /// key from each <see cref="SystemSignalKind.AreaLoading"/> envelope
    /// observed on the unified pipe. The tracker tracks its own area state
    /// locally rather than reading from <c>PlayerAreaTracker</c>, so the
    /// reconcile never depends on a separate pump's progress.</param>
    /// <param name="diag">Optional diagnostics sink (info on subscribe,
    /// warnings on ingestion / subscriber faults).</param>
    public PlayerPinTracker(
        ILogStreamDriver driver,
        MapPinParser parser,
        AreaTransitionParser areaParser,
        IDiagnosticsSink? diag = null)
    {
        _driver = driver;
        _parser = parser;
        _areaParser = areaParser;
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
            // PlayerPositionTracker.Subscribe. The stamp comes from the
            // most-recent envelope the tracker has applied (NOT wall-clock
            // — principle 13); see _lastObservedAt's field doc.
            Invoke(handler, new PinSetChanged(
                PinSetChange.Snapshot, _trackedArea, null, Snapshot(),
                _lastObservedAt));
            _handlers.Add(handler);
            return new Subscription(this, handler);
        }
    }

    /// <summary>
    /// The hosted-service entry point. Opens an L1 subscription to the
    /// unified classified pipe and parks until host shutdown — the L1
    /// subscription runs its own pump on a <see cref="Task.Run(Func{Task})"/>,
    /// invoking the handler inline for each envelope in source order.
    /// </summary>
    /// <param name="stoppingToken">Host shutdown signal.</param>
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _diag?.Info(DiagCategory,
            "Subscribing to L1 driver (unified classified pipe) for ProcessMapPin* + area reconcile");

        _subscription = _driver.Subscribe<IClassifiedPlayerLogLine>(
            envelope =>
            {
                // Both classes of envelopes arrive in source order on the
                // unified pipe, so a LOADING LEVEL always lands before its
                // following pin-replay burst — no cross-pump race.
                switch (envelope.Payload)
                {
                    case SystemSignalLogLine { Kind: SystemSignalKind.AreaLoading } s:
                        // The area parser handles the empty / ChooseCharacter
                        // forms by returning AreaKey == null.
                        if (_areaParser.TryParse(s.Data, s.Timestamp.UtcDateTime)
                            is Areas.Parsing.AreaTransitionEvent areaEvt)
                        {
                            ReconcileArea(areaEvt.AreaKey, s.Timestamp);
                        }
                        break;

                    case LocalPlayerLogLine l:
                        if (_parser.TryParse(l.Data, l.Timestamp.UtcDateTime)
                            is MapPinLogEvent pinEvt)
                        {
                            Apply(pinEvt);
                        }
                        break;

                    // CombatActorLogLine / other SystemSignal kinds silently
                    // no-op. Combat envelopes are rare on this consumer set;
                    // a default warning would flood the diag log.
                }
                return ValueTask.CompletedTask;
            },
            new LogSubscriptionOptions
            {
                ReplayMode = ReplayMode.FromSessionStart,
                DeliveryContext = DeliveryContext.Inline,
                DiagnosticCategory = DiagCategory,
            });

        try { await Task.Delay(Timeout.Infinite, stoppingToken).ConfigureAwait(false); }
        catch (OperationCanceledException) { /* expected on host stop */ }
        finally
        {
            _subscription?.Dispose();
            _subscription = null;
        }
    }

    public override void Dispose()
    {
        _subscription?.Dispose();
        _subscription = null;
        base.Dispose();
    }

    /// <summary>
    /// Drop the set whenever the area key changes (including the first
    /// time it becomes known). The set is area-local; the new area's replay
    /// burst — which follows the <c>LOADING LEVEL</c> envelope that moved
    /// the key — repopulates it. Called from the unified-pipe handler when a
    /// new <see cref="SystemSignalKind.AreaLoading"/> envelope arrives.
    /// </summary>
    /// <param name="area">New area key (null for the empty / ChooseCharacter
    /// form).</param>
    /// <param name="observedAt">Envelope timestamp of the triggering
    /// <c>LOADING LEVEL</c> system-signal — stamped on the resulting
    /// <see cref="PinSetChange.AreaChanged"/> notification so consumers see
    /// log-instant time, never wall clock (principle 13).</param>
    private void ReconcileArea(string? area, DateTimeOffset observedAt)
    {
        PinSetChanged? note = null;
        lock (_lock)
        {
            if (_areaInitialised && area == _trackedArea) return;
            _areaInitialised = true;
            _trackedArea = area;
            _pins.Clear();
            _lastObservedAt = observedAt;
            note = new PinSetChanged(
                PinSetChange.AreaChanged, area, null, Snapshot(),
                observedAt);
        }
        if (note is not null) Publish(note);
    }

    private void Apply(MapPinLogEvent evt)
    {
        var key = PinKey.From(evt.X, evt.Z);
        var observedAt = ToOffset(evt.Timestamp);
        PinSetChanged? note = null;
        lock (_lock)
        {
            if (evt.Change == MapPinChange.Removed)
            {
                if (_pins.Remove(key, out var removed))
                {
                    _lastObservedAt = observedAt;
                    note = new PinSetChanged(
                        PinSetChange.Removed, _trackedArea, removed, Snapshot(),
                        observedAt);
                }
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
                    _lastObservedAt = observedAt;
                    note = new PinSetChanged(
                        PinSetChange.Added, _trackedArea, pin, Snapshot(),
                        observedAt);
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
