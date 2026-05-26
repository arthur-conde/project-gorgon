using Arda.Dispatch;
using Mithril.Shared.Diagnostics;
using Microsoft.Extensions.Hosting;

using ArdaWeatherChanged = Arda.World.Player.Events.WeatherChanged;
using ArdaAreaChanged = Arda.World.Player.Events.AreaChanged;

namespace Mithril.GameState.Weather;

/// <summary>
/// Hosted-service implementation of <see cref="IPlayerWeatherTracker"/>.
/// Subscribes to Arda domain events (<see cref="ArdaWeatherChanged"/> and
/// <see cref="ArdaAreaChanged"/>) and holds the current <b>map's</b>
/// <see cref="WeatherState"/> (condition + the line's UTC instant).
///
/// <para><b>Lifecycle the service owns</b> (so no consumer hand-rolls it) —
/// the same area-scoped shape as <see cref="Pins.PlayerPinTracker"/> because
/// weather is per-map (owner-confirmed):</para>
/// <list type="bullet">
///   <item><b>Map change = drop.</b> Weather belongs to the map; the
///   tracker subscribes to <see cref="ArdaAreaChanged"/> events and clears
///   the value on any change, raising a
///   <see cref="WeatherChangeKind.AreaChanged"/> (<c>State == null</c>).
///   <c>null</c> reads as "weather unknown for this map" — for the
///   <c>Vampirism</c> consumer that is distinct from a known clear sky, so
///   a stale foggy/sunny value never bleeds across a zone.</item>
///   <item><b>Genuine change = update + notify.</b> A new condition for
///   the current map updates <see cref="Current"/> and raises
///   <see cref="WeatherChangeKind.Changed"/>.</item>
///   <item><b>Idempotent re-emit.</b> Arda's weather handler already
///   deduplicates same-condition re-emissions, so the domain event fires
///   only on genuine transitions.</item>
/// </list>
///
/// <para><b>Threading.</b> The Arda domain event bus invokes handlers on
/// its dispatch thread; <see cref="CurrentArea"/>/<see cref="Current"/>
/// reads, state mutation and subscriber dispatch are serialised under
/// <c>_lock</c>; each notification carries an immutable record, so handlers
/// may hold it. They run on the Arda dispatch thread — non-trivial / UI work
/// must marshal off (mirrors PlayerPinTracker).</para>
/// </summary>
public sealed class PlayerWeatherTracker : BackgroundService, IPlayerWeatherTracker
{
    private readonly IDomainEventSubscriber _bus;
    private readonly IDiagnosticsSink? _diag;

    private readonly object _lock = new();
    private readonly List<Action<WeatherChanged>> _handlers = [];
    private WeatherState? _current;
    private string? _trackedArea;
    private bool _areaInitialised;
    private IDisposable? _weatherSub;
    private IDisposable? _areaSub;

    // Most-recent event timestamp the tracker has applied (area
    // transition or weather change). Subscribe-replay stamps its synthetic
    // Snapshot notification with this so late subscribers observe the same
    // event-time view already-attached handlers were given (principle 13:
    // never leak wall clock into world-event-driven state). Mirrors
    // PlayerPinTracker._lastObservedAt. Default (DateTimeOffset.MinValue)
    // when no event has been applied yet — the snapshot payload is also
    // empty in that case, so the stamp is never meaningful in isolation.
    private DateTimeOffset _lastObservedAt;

    private const string DiagCategory = "GameState.Weather";

    /// <param name="bus">Arda domain event subscriber — receives
    /// <see cref="ArdaWeatherChanged"/> and <see cref="ArdaAreaChanged"/>
    /// events from the world-sim dispatch pipeline.</param>
    /// <param name="diag">Optional diagnostics sink (info on subscribe,
    /// warnings on subscriber faults).</param>
    public PlayerWeatherTracker(
        IDomainEventSubscriber bus,
        IDiagnosticsSink? diag = null)
    {
        _bus = bus;
        _diag = diag;
    }

    /// <inheritdoc/>
    public string? CurrentArea
    {
        get { lock (_lock) return _trackedArea; }
    }

    /// <inheritdoc/>
    public WeatherState? Current
    {
        get { lock (_lock) return _current; }
    }

    /// <inheritdoc/>
    public IDisposable Subscribe(Action<WeatherChanged> handler)
    {
        ArgumentNullException.ThrowIfNull(handler);
        lock (_lock)
        {
            Invoke(handler, new WeatherChanged(
                WeatherChangeKind.Snapshot, _trackedArea, _current,
                _lastObservedAt));
            _handlers.Add(handler);
            return new Subscription(this, handler);
        }
    }

    public override Task StartAsync(CancellationToken cancellationToken)
    {
        _diag?.Info(DiagCategory,
            "Subscribing to Arda WeatherChanged + AreaChanged domain events");

        _areaSub = _bus.Subscribe<ArdaAreaChanged>(OnAreaChanged);
        _weatherSub = _bus.Subscribe<ArdaWeatherChanged>(OnWeatherChanged);
        return base.StartAsync(cancellationToken);
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try { await Task.Delay(Timeout.Infinite, stoppingToken).ConfigureAwait(false); }
        catch (OperationCanceledException) { /* expected on host stop */ }
        finally
        {
            _weatherSub?.Dispose();
            _weatherSub = null;
            _areaSub?.Dispose();
            _areaSub = null;
        }
    }

    public override void Dispose()
    {
        _weatherSub?.Dispose();
        _weatherSub = null;
        _areaSub?.Dispose();
        _areaSub = null;
        base.Dispose();
    }

    private void OnAreaChanged(ArdaAreaChanged evt)
    {
        var observedAt = evt.Metadata.Timestamp ?? evt.Metadata.ReadOn;
        ReconcileArea(evt.CurrentArea, observedAt);
    }

    private void OnWeatherChanged(ArdaWeatherChanged evt)
    {
        var observedAt = evt.Metadata.Timestamp ?? evt.Metadata.ReadOn;
        Apply(evt.Current, observedAt);
    }

    /// <summary>
    /// Drop the weather whenever the map key changes (including the first
    /// time it becomes known). Weather is per-map; the new map's
    /// <c>ProcessSetWeather</c> — which follows the area change in the
    /// Arda dispatch pipeline — repopulates it.
    /// </summary>
    private void ReconcileArea(string? area, DateTimeOffset observedAt)
    {
        WeatherChanged? note = null;
        lock (_lock)
        {
            if (_areaInitialised && area == _trackedArea) return;
            _areaInitialised = true;
            _trackedArea = area;
            _current = null;
            _lastObservedAt = observedAt;
            note = new WeatherChanged(
                WeatherChangeKind.AreaChanged, area, null,
                observedAt);
        }
        if (note is not null) Publish(note);
    }

    private void Apply(string condition, DateTimeOffset observedAt)
    {
        WeatherChanged? note = null;
        lock (_lock)
        {
            if (_current is { } cur
                && string.Equals(cur.Condition, condition, StringComparison.Ordinal))
            {
                return;
            }

            // Arda's WeatherChanged event does not carry the opaque
            // boolean flag from the raw log line (semantics unverified;
            // see WeatherChangedEvent.Flag doc). Default to false to
            // preserve the WeatherState record shape.
            _current = new WeatherState(condition, Flag: false, observedAt);
            _lastObservedAt = observedAt;
            note = new WeatherChanged(
                WeatherChangeKind.Changed, _trackedArea, _current,
                observedAt);
        }
        if (note is not null) Publish(note);
    }

    private void Publish(WeatherChanged note)
    {
        Action<WeatherChanged>[] toFire;
        lock (_lock) toFire = _handlers.ToArray();
        foreach (var h in toFire) Invoke(h, note);
    }

    private void Invoke(Action<WeatherChanged> handler, WeatherChanged note)
    {
        try { handler(note); }
        catch (Exception ex) { _diag?.Warn("GameState.Weather", $"Subscriber threw: {ex.Message}"); }
    }

    private sealed class Subscription : IDisposable
    {
        private PlayerWeatherTracker? _owner;
        private readonly Action<WeatherChanged> _handler;

        public Subscription(PlayerWeatherTracker owner, Action<WeatherChanged> handler)
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
