using Arda.Abstractions.Logs;
using Arda.Dispatch;
using Arda.World.Player.Events;
using Mithril.Shared.Diagnostics;

namespace Mithril.GameState.Pins;

/// <summary>
/// Implementation of <see cref="IPlayerPinTracker"/> driven by Arda domain
/// events. Subscribes to <see cref="MapPinAdded"/>, <see cref="MapPinRemoved"/>,
/// and <see cref="AreaChanged"/> on the <see cref="IDomainEventSubscriber"/>
/// bus, and folds the stream into the current area's pin set (#468).
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
///   subscribes to <see cref="AreaChanged"/> and clears the set on any
///   transition, raising a <see cref="PinSetChange.AreaChanged"/> (empty set).
///   The subsequent replay burst repopulates it for the new area.</item>
/// </list>
///
/// <para><b>Why domain events.</b> The Arda world simulator's dispatch layer
/// provides a single ordered stream of typed domain events: the
/// <see cref="AreaChanged"/> event always precedes the pin-replay burst for
/// that area (same source ordering as the underlying log), so the area
/// reconcile always fires before the pins — no cross-pump race.</para>
///
/// <para><b>Threading.</b> Domain event handlers fire on the Arda dispatch
/// thread; <see cref="CurrentArea"/>/<see cref="CurrentAreaPins"/> reads,
/// state mutation, and subscriber dispatch are serialised under
/// <c>_lock</c>; each notification carries an immutable snapshot, so
/// handlers may hold it. They run on the dispatch thread — non-trivial / UI
/// work must marshal off.</para>
/// </summary>
public sealed class PlayerPinTracker : IPlayerPinTracker, IDisposable
{
    private readonly IDomainEventSubscriber _domainBus;
    private readonly IDiagnosticsSink? _diag;

    private readonly object _lock = new();
    private readonly List<Action<PinSetChanged>> _handlers = [];

    private readonly Dictionary<PinKey, MapPin> _pins = new();
    private string? _trackedArea;
    private bool _areaInitialised;

    private IDisposable? _pinAddedSub;
    private IDisposable? _pinRemovedSub;
    private IDisposable? _areaChangedSub;
    private bool _started;
    private bool _disposed;

    // Most-recent event timestamp the tracker has applied (area transition or
    // pin add/remove). Subscribe-replay stamps its synthetic Snapshot
    // notification with this so late subscribers observe the same event-time
    // view already-attached handlers were given (principle 13: never leak
    // wall clock into world-event-driven state).
    private DateTimeOffset _lastObservedAt;

    private const string DiagCategory = "GameState.Pins";

    public PlayerPinTracker(
        IDomainEventSubscriber domainBus,
        IDiagnosticsSink? diag = null)
    {
        _domainBus = domainBus;
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
            Invoke(handler, new PinSetChanged(
                PinSetChange.Snapshot, _trackedArea, null, Snapshot(),
                _lastObservedAt));
            _handlers.Add(handler);
            return new Subscription(this, handler);
        }
    }

    /// <summary>
    /// Attach to the Arda domain event bus. Idempotent — the registration
    /// hosted service calls this once during host start; calling it twice
    /// is safe.
    /// </summary>
    public void Start()
    {
        if (_started) return;
        _started = true;

        _pinAddedSub = _domainBus.Subscribe<MapPinAdded>(OnPinAdded);
        _pinRemovedSub = _domainBus.Subscribe<MapPinRemoved>(OnPinRemoved);
        _areaChangedSub = _domainBus.Subscribe<AreaChanged>(OnAreaChanged);

        _diag?.Info(DiagCategory,
            "PlayerPinTracker subscribed to Arda domain bus (MapPinAdded + MapPinRemoved + AreaChanged)");
    }

    private void OnAreaChanged(AreaChanged evt)
    {
        var observedAt = EventTimestamp(evt.Metadata);
        PinSetChanged? note = null;
        lock (_lock)
        {
            if (_areaInitialised && evt.CurrentArea == _trackedArea) return;
            _areaInitialised = true;
            _trackedArea = evt.CurrentArea;
            _pins.Clear();
            _lastObservedAt = observedAt;
            note = new PinSetChanged(
                PinSetChange.AreaChanged, evt.CurrentArea, null, Snapshot(),
                observedAt);
        }
        if (note is not null) Publish(note);
    }

    private void OnPinAdded(MapPinAdded evt)
    {
        var key = PinKey.From(evt.X, evt.Z);
        var observedAt = EventTimestamp(evt.Metadata);
        var pin = new MapPin(
            evt.X, evt.Z, evt.Label,
            evt.Shape.ToPinShape(), evt.Color.ToPinColor(),
            RawList: 1);

        PinSetChanged? note = null;
        lock (_lock)
        {
            if (!_pins.TryGetValue(key, out var existing) || existing != pin)
            {
                _pins[key] = pin;
                _lastObservedAt = observedAt;
                note = new PinSetChanged(
                    PinSetChange.Added, _trackedArea, pin, Snapshot(),
                    observedAt);
            }
        }
        if (note is not null) Publish(note);
    }

    private void OnPinRemoved(MapPinRemoved evt)
    {
        var key = PinKey.From(evt.X, evt.Z);
        var observedAt = EventTimestamp(evt.Metadata);
        PinSetChanged? note = null;
        lock (_lock)
        {
            if (_pins.Remove(key, out var removed))
            {
                _lastObservedAt = observedAt;
                note = new PinSetChanged(
                    PinSetChange.Removed, _trackedArea, removed, Snapshot(),
                    observedAt);
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
        catch (Exception ex) { _diag?.Warn(DiagCategory, $"Subscriber threw: {ex.Message}"); }
    }

    /// <summary>
    /// Extract the canonical event timestamp from Arda metadata (log-line
    /// timestamp when available, wall-clock <see cref="LogLineMetadata.ReadOn"/>
    /// as fallback). Keeps the offset from the source — Player.log timestamps
    /// are UTC (+00:00).
    /// </summary>
    private static DateTimeOffset EventTimestamp(LogLineMetadata metadata) =>
        metadata.Timestamp ?? metadata.ReadOn;

    private readonly record struct PinKey(long X100, long Z100)
    {
        public static PinKey From(double x, double z) => new(
            (long)Math.Round(x * 100.0, MidpointRounding.AwayFromZero),
            (long)Math.Round(z * 100.0, MidpointRounding.AwayFromZero));
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _pinAddedSub?.Dispose();
        _pinRemovedSub?.Dispose();
        _areaChangedSub?.Dispose();
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
