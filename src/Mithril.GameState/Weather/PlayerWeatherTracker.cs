using Mithril.GameState.Areas.Parsing;
using Mithril.Shared.Diagnostics;
using Mithril.Shared.Logging;
using Microsoft.Extensions.Hosting;

namespace Mithril.GameState.Weather;

/// <summary>
/// Hosted-service implementation of <see cref="IPlayerWeatherTracker"/>.
/// Subscribes to L1's unified classified pipe (#556 Phase 3), parses
/// <c>ProcessSetWeather</c> on the <see cref="LocalPlayerLogLine"/>
/// payload via <see cref="WeatherLogParser"/>, and holds the current
/// <b>map's</b> <see cref="WeatherState"/> (condition + opaque flag + the
/// line's UTC instant).
///
/// <para><b>Lifecycle the service owns</b> (so no consumer hand-rolls it) —
/// the same area-scoped shape as <see cref="Pins.PlayerPinTracker"/> because
/// weather is per-map (owner-confirmed):</para>
/// <list type="bullet">
///   <item><b>Map change = drop.</b> Weather belongs to the map; the
///   tracker parses <see cref="SystemSignalKind.AreaLoading"/> envelopes
///   off the unified pipe and clears the value on any change, raising a
///   <see cref="WeatherChangeKind.AreaChanged"/> (<c>State == null</c>).
///   <c>null</c> reads as "weather unknown for this map" — for the
///   <c>Vampirism</c> consumer that is distinct from a known clear sky, so
///   a stale foggy/sunny value never bleeds across a zone.</item>
///   <item><b>Genuine change = update + notify.</b> A new condition/flag for
///   the current map updates <see cref="Current"/> and raises
///   <see cref="WeatherChangeKind.Changed"/>.</item>
///   <item><b>Idempotent re-emit.</b> If PG re-emits the current weather on
///   zone entry (replay — unverified but plausible), the repeat is a no-op and
///   raises nothing, so consumers stay quiet through a backlog.</item>
/// </list>
///
/// <para><b>Why the unified pipe — the race fix.</b> Pre-#556 this tracker
/// read <c>PlayerAreaTracker.CurrentArea</c> for the map reconcile and
/// tailed the raw Player.log for weather events. A zone-change burst
/// (<c>LOADING LEVEL Foo</c> immediately followed by
/// <c>ProcessSetWeather Sunny</c>) could be processed by the two pumps
/// out of order — Weather seeing the new weather before the area tracker
/// had advanced — and the resulting reconcile would drop the just-set
/// value as a "stale prior-area" emit. Subscribing to the L0.5 unified
/// classified pipe puts both envelopes on one ordered stream through a
/// single subscription, so the area reconcile always precedes the weather
/// emit for that area. Combat envelopes silently no-op.</para>
///
/// <para><b>Threading.</b> The L1 driver's <c>InlineBridge</c> invokes the
/// handler on its pump thread; <see cref="CurrentArea"/>/<see cref="Current"/>
/// reads, state mutation and subscriber dispatch are serialised under
/// <c>_lock</c>; each notification carries an immutable record, so handlers
/// may hold it. They run on the L1 pump thread — non-trivial / UI work
/// must marshal off (mirrors PlayerPinTracker).</para>
/// </summary>
public sealed class PlayerWeatherTracker : BackgroundService, IPlayerWeatherTracker
{
    private readonly ILogStreamDriver _driver;
    private readonly WeatherLogParser _parser;
    private readonly AreaTransitionParser _areaParser;
    private readonly IDiagnosticsSink? _diag;

    private readonly object _lock = new();
    private readonly List<Action<WeatherChanged>> _handlers = [];
    private WeatherState? _current;
    private string? _trackedArea;
    private bool _areaInitialised;
    private ILogSubscription? _subscription;

    // Most-recent envelope timestamp the tracker has applied (area
    // transition or weather change). Subscribe-replay stamps its synthetic
    // Snapshot notification with this so late subscribers observe the same
    // envelope-time view already-attached handlers were given (principle 13:
    // never leak wall clock into world-event-driven state). Mirrors
    // PlayerPinTracker._lastObservedAt. Default (DateTimeOffset.MinValue)
    // when no envelope has been applied yet — the snapshot payload is also
    // empty in that case, so the stamp is never meaningful in isolation.
    private DateTimeOffset _lastObservedAt;

    private const string DiagCategory = "GameState.Weather";

    /// <param name="driver">L1 driver — subscribed to via the
    /// <see cref="IClassifiedPlayerLogLine"/> unified pipe.</param>
    /// <param name="parser">Stateless line→<see cref="WeatherChangedEvent"/> parser.</param>
    /// <param name="areaParser">Stateless parser used to extract the area
    /// key from each <see cref="SystemSignalKind.AreaLoading"/> envelope
    /// observed on the unified pipe. The tracker tracks its own area state
    /// locally rather than reading from <c>PlayerAreaTracker</c>, so the
    /// reconcile never depends on a separate pump's progress — closing the
    /// pre-#556 zone-change race.</param>
    /// <param name="diag">Optional diagnostics sink (info on subscribe,
    /// warnings on ingestion / subscriber faults).</param>
    public PlayerWeatherTracker(
        ILogStreamDriver driver,
        WeatherLogParser parser,
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
            // Replay the current state before going live so a late subscriber
            // observes the same view already-attached handlers see. Mirrors
            // PlayerPinTracker.Subscribe. The stamp comes from the
            // most-recent envelope the tracker has applied (NOT wall-clock
            // — principle 13); see _lastObservedAt's field doc.
            Invoke(handler, new WeatherChanged(
                WeatherChangeKind.Snapshot, _trackedArea, _current,
                _lastObservedAt));
            _handlers.Add(handler);
            return new Subscription(this, handler);
        }
    }

    /// <summary>
    /// The hosted-service entry point. Opens an L1 subscription to the
    /// unified classified pipe and parks until host shutdown — the L1
    /// subscription runs its own pump, invoking the handler inline for
    /// each envelope in source order.
    /// </summary>
    /// <param name="stoppingToken">Host shutdown signal.</param>
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _diag?.Info(DiagCategory,
            "Subscribing to L1 driver (unified classified pipe) for ProcessSetWeather + area reconcile");

        _subscription = _driver.Subscribe<IClassifiedPlayerLogLine>(
            envelope =>
            {
                switch (envelope.Payload)
                {
                    case SystemSignalLogLine { Kind: SystemSignalKind.AreaLoading } s:
                        if (_areaParser.TryParse(s.Data, s.Timestamp.UtcDateTime)
                            is AreaTransitionEvent areaEvt)
                        {
                            ReconcileArea(areaEvt.AreaKey, s.Timestamp);
                        }
                        break;

                    case LocalPlayerLogLine l:
                        if (_parser.TryParse(l.Data, l.Timestamp.UtcDateTime)
                            is WeatherChangedEvent weatherEvt)
                        {
                            Apply(weatherEvt);
                        }
                        break;

                    // CombatActorLogLine / other SystemSignal kinds silently
                    // no-op (combat envelopes on the unified pipe — by
                    // design; a default warning would flood the diag log).
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
    /// Drop the weather whenever the map key changes (including the first
    /// time it becomes known). Weather is per-map; the new map's
    /// <c>ProcessSetWeather</c> — which follows the <c>LOADING LEVEL</c>
    /// envelope on the same unified pipe — repopulates it.
    /// </summary>
    /// <param name="area">New area key (null for the empty / ChooseCharacter
    /// form).</param>
    /// <param name="observedAt">Envelope timestamp of the triggering
    /// <c>LOADING LEVEL</c> system-signal — stamped on the resulting
    /// <see cref="WeatherChangeKind.AreaChanged"/> notification so
    /// consumers see log-instant time, never wall clock (principle 13).</param>
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

    private void Apply(WeatherChangedEvent evt)
    {
        var observedAt = ToOffset(evt.Timestamp);
        WeatherChanged? note = null;
        lock (_lock)
        {
            // Idempotent: a replay re-emit of the unchanged weather for the
            // current map is a no-op and notifies nothing.
            if (_current is { } cur
                && string.Equals(cur.Condition, evt.Condition, StringComparison.Ordinal)
                && cur.Flag == evt.Flag)
            {
                return;
            }

            _current = new WeatherState(evt.Condition, evt.Flag, observedAt);
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

    /// <summary>
    /// Player.log timestamps are reconstructed as UTC <see cref="DateTime"/>s.
    /// Stamp the kind defensively so the offset is +00:00 rather than the
    /// host's local offset (mirrors PlayerPinTracker.ToOffset).
    /// </summary>
    private static DateTimeOffset ToOffset(DateTime ts) =>
        new(DateTime.SpecifyKind(ts, DateTimeKind.Utc));

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
