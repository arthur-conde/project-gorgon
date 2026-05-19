using Mithril.GameState.Areas;
using Mithril.Shared.Diagnostics;
using Mithril.Shared.Logging;
using Microsoft.Extensions.Hosting;

namespace Mithril.GameState.Weather;

/// <summary>
/// Hosted-service implementation of <see cref="IPlayerWeatherTracker"/>. Tails
/// <see cref="IPlayerLogStream"/>, parses <c>ProcessSetWeather</c> via
/// <see cref="WeatherLogParser"/>, and holds the current <b>map's</b>
/// <see cref="WeatherState"/> (condition + opaque flag + the line's UTC
/// instant).
///
/// <para><b>Lifecycle the service owns</b> (so no consumer hand-rolls it) —
/// the same area-scoped shape as <see cref="Pins.PlayerPinTracker"/> because
/// weather is per-map (owner-confirmed):</para>
/// <list type="bullet">
///   <item><b>Map change = drop.</b> Weather belongs to the map; on any
///   <c>PlayerAreaTracker.CurrentArea</c> change the value is cleared and a
///   <see cref="WeatherChangeKind.AreaChanged"/> (<c>State == null</c>) is
///   raised. <c>null</c> reads as "weather unknown for this map" — for the
///   <c>Vampirism</c> consumer that is distinct from a known clear sky, so a
///   stale foggy/sunny value never bleeds across a zone.</item>
///   <item><b>Genuine change = update + notify.</b> A new condition/flag for
///   the current map updates <see cref="Current"/> and raises
///   <see cref="WeatherChangeKind.Changed"/>.</item>
///   <item><b>Idempotent re-emit.</b> If PG re-emits the current weather on
///   zone entry (replay — unverified but plausible), the repeat is a no-op and
///   raises nothing, so consumers stay quiet through a backlog (the same
///   idempotence stance as <c>PlayerPinTracker</c>'s replay burst).</item>
/// </list>
///
/// <para><b>Why a self-feeding BackgroundService.</b> Mirrors
/// <see cref="Pins.PlayerPinTracker"/> / <see cref="Movement.PlayerPositionTracker"/>:
/// shared live game-state must be available to consumers (the future
/// <c>Vampirism</c> sun-damage feature; Palantir's debug surface) without
/// depending on any module being activated. It also calls
/// <see cref="PlayerAreaTracker.Observe(RawLogLine)"/> to keep the shared area
/// tracker warm (idempotent when the position/pin trackers also feed it). No
/// reverse-scan seed is needed here: the shared <see cref="PlayerAreaTracker"/>
/// singleton is already seeded at startup by the position tracker, and a
/// per-map weather emit lands inside the live window after its
/// <c>LOADING LEVEL</c> line.</para>
///
/// <para><b>Threading.</b> Ingestion runs on the hosted-service loop thread;
/// <see cref="CurrentArea"/>/<see cref="Current"/> reads, state mutation and
/// subscriber dispatch are serialised under <c>_lock</c>; each notification
/// carries an immutable record, so handlers may hold it. They run on the
/// ingestion thread — non-trivial / UI work must marshal off (mirrors
/// PlayerPinTracker).</para>
/// </summary>
public sealed class PlayerWeatherTracker : BackgroundService, IPlayerWeatherTracker
{
    private readonly IPlayerLogStream _stream;
    private readonly WeatherLogParser _parser;
    private readonly PlayerAreaTracker _areaTracker;
    private readonly IDiagnosticsSink? _diag;

    private readonly object _lock = new();
    private readonly List<Action<WeatherChanged>> _handlers = [];
    private WeatherState? _current;
    private string? _trackedArea;
    private bool _areaInitialised;

    /// <param name="stream">The shared Player.log line stream this service tails.</param>
    /// <param name="parser">Stateless line→<see cref="WeatherChangedEvent"/> parser.</param>
    /// <param name="areaTracker">Shared map key source; also fed every line
    /// here so it stays warm without another module being active.</param>
    /// <param name="diag">Optional diagnostics sink (info on subscribe,
    /// warnings on ingestion / subscriber faults).</param>
    public PlayerWeatherTracker(
        IPlayerLogStream stream,
        WeatherLogParser parser,
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
            // PlayerPinTracker.Subscribe.
            Invoke(handler, new WeatherChanged(
                WeatherChangeKind.Snapshot, _trackedArea, _current,
                DateTimeOffset.UtcNow));
            _handlers.Add(handler);
            return new Subscription(this, handler);
        }
    }

    /// <summary>
    /// The hosted-service ingestion loop: for every Player.log line, warm the
    /// shared area tracker, reconcile a map change, then fold a parsed weather
    /// event into the current map's state. Exits when
    /// <paramref name="stoppingToken"/> is cancelled or the stream completes;
    /// per-line exceptions are logged and swallowed so one bad line never
    /// stops ingestion.
    /// </summary>
    /// <param name="stoppingToken">Host shutdown signal.</param>
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _diag?.Info("GameState.Weather", "Subscribing to Player.log for ProcessSetWeather");
        await foreach (var raw in _stream.SubscribeAsync(stoppingToken).ConfigureAwait(false))
        {
            try
            {
                // Same line stream carries map transitions; keep the shared
                // tracker warm even when no other feeder is active.
                _areaTracker.Observe(raw);
                ReconcileArea();

                if (_parser.TryParse(raw.Line, raw.Timestamp.UtcDateTime) is WeatherChangedEvent evt)
                    Apply(evt);
            }
            catch (Exception ex)
            {
                _diag?.Warn("GameState.Weather", $"Ingestion error: {ex.Message}");
            }
        }
    }

    /// <summary>
    /// Drop the weather whenever the shared map key changes (including the
    /// first time it becomes known). Weather is per-map; the new map's
    /// <c>ProcessSetWeather</c> — which follows the <c>LOADING LEVEL</c> line
    /// that moved the key — repopulates it.
    /// </summary>
    private void ReconcileArea()
    {
        var area = _areaTracker.CurrentArea;
        WeatherChanged? note = null;
        lock (_lock)
        {
            if (_areaInitialised && area == _trackedArea) return;
            _areaInitialised = true;
            _trackedArea = area;
            _current = null;
            note = new WeatherChanged(
                WeatherChangeKind.AreaChanged, area, null,
                DateTimeOffset.UtcNow);
        }
        if (note is not null) Publish(note);
    }

    private void Apply(WeatherChangedEvent evt)
    {
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

            _current = new WeatherState(evt.Condition, evt.Flag, ToOffset(evt.Timestamp));
            note = new WeatherChanged(
                WeatherChangeKind.Changed, _trackedArea, _current,
                ToOffset(evt.Timestamp));
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
