using System.Diagnostics.Tracing;
using System.Globalization;

namespace Mithril.Shared.Telemetry.Export;

/// <summary>
/// Subscribes to the OpenTelemetry OTLP exporter's internal
/// <see cref="EventSource"/> (<c>OpenTelemetry-Exporter-OpenTelemetryProtocol</c>)
/// and feeds Warning/Error/Critical events into
/// <see cref="ExporterHealthMonitor.RecordFailure(string)"/> so the settings
/// status line shows real OTLP transport failures rather than a perpetual
/// "no activity yet".
///
/// <para>The OTel SDK exposes no public success callback, so success is
/// synthesised by an absence-of-failure heuristic: a <see cref="Timer"/>
/// ticks once per <see cref="DefaultSuccessTickInterval"/> and, when the
/// most-recent failure is older than the tick window, calls
/// <see cref="ExporterHealthMonitor.RecordSuccess"/>. Tooltip wording in the
/// settings UI is "last successful export attempt" — not "last successful
/// delivery" — to reflect this: a collector that ACKs and then silently drops
/// data downstream of OTLP still presents as healthy from this listener's
/// vantage point. Tradeoff per mithril#834.</para>
///
/// <para><strong>Pin: validated against
/// <c>OpenTelemetry.Exporter.OpenTelemetryProtocol 1.15.x</c>.</strong>
/// The exporter's event-source <em>name</em> is the stable surface; specific
/// event ids and message templates are internal SDK detail and shift across
/// minor versions. This listener intentionally maps by <see cref="EventLevel"/>
/// rather than event id, so a renovate bump that renumbers events does not
/// silently regress the health monitor. A bump that renames the event source
/// itself, or moves failures off Warning/Error/Critical levels, must
/// re-validate against the new SDK source.</para>
///
/// <para>Hosted-service ordering: the listener self-subscribes in its
/// constructor via the base <see cref="EventListener"/> registry, so callers
/// only need to ensure it is constructed before any export attempt. The
/// telemetry host extension resolves the singleton eagerly at host build
/// time to guarantee this — startup-time failures (corrupted endpoint, DNS,
/// TLS) are then captured the first time the exporter probes the
/// connection.</para>
///
/// <para><strong>Construction race.</strong>
/// <see cref="EventListener.OnEventSourceCreated(EventSource)"/> may fire for
/// already-existing sources before this listener's constructor body runs —
/// the base <see cref="EventListener"/> constructor walks the active source
/// list. The implementation defers <see cref="EventListener.EnableEvents(EventSource, EventLevel)"/>
/// until <see cref="Initialize"/> has stored the monitor reference, queueing
/// any sources discovered during the race.</para>
/// </summary>
public sealed class OtlpExporterEventListener : EventListener
{
    /// <summary>
    /// The internal OTel SDK event source emitted by the OTLP exporter.
    /// Stable across the 1.15.x line; see class-level remarks.
    /// </summary>
    public const string OtlpExporterEventSourceName = "OpenTelemetry-Exporter-OpenTelemetryProtocol";

    /// <summary>
    /// Default cadence at which absence-of-failure is converted into a
    /// success pulse. Chosen to be slow enough not to mask transient failures
    /// (which the exporter retries internally) and fast enough to flip the
    /// status line back to green within a few minutes of a collector recovery.
    /// </summary>
    public static readonly TimeSpan DefaultSuccessTickInterval = TimeSpan.FromSeconds(30);

    private readonly object _initLock = new();
    private readonly List<EventSource> _pendingSources = new();
    private ExporterHealthMonitor? _health;
    private TimeSpan _successInterval;
    private Timer? _successTimer;
    private bool _initialized;
    private long _lastFailureUtcTicks; // 0 ⇒ no failure yet

    /// <summary>
    /// Production constructor. Subscribes immediately; success ticks use
    /// <see cref="DefaultSuccessTickInterval"/>.
    /// </summary>
    public OtlpExporterEventListener(ExporterHealthMonitor health)
        : this(health, DefaultSuccessTickInterval, startTimer: true)
    {
    }

    /// <summary>
    /// Test-friendly constructor. When <paramref name="startTimer"/> is
    /// <c>false</c>, the background <see cref="Timer"/> is not created and
    /// tests drive success synthesis by calling
    /// <see cref="TickSuccessForTests"/> directly.
    /// </summary>
    internal OtlpExporterEventListener(
        ExporterHealthMonitor health,
        TimeSpan successInterval,
        bool startTimer)
    {
        Initialize(health, successInterval, startTimer);
    }

    private void Initialize(ExporterHealthMonitor health, TimeSpan successInterval, bool startTimer)
    {
        EventSource[] toEnable;
        lock (_initLock)
        {
            _health = health;
            _successInterval = successInterval;
            _initialized = true;
            toEnable = _pendingSources.ToArray();
            _pendingSources.Clear();
        }
        foreach (var src in toEnable)
        {
            EnableEvents(src, EventLevel.Warning);
        }
        if (startTimer)
        {
            _successTimer = new Timer(_ => SafeTickSuccess(), state: null, successInterval, successInterval);
        }
    }

    /// <inheritdoc />
    protected override void OnEventSourceCreated(EventSource eventSource)
    {
        if (eventSource is null) return;
        if (eventSource.Name != OtlpExporterEventSourceName) return;

        lock (_initLock)
        {
            if (!_initialized)
            {
                // Construction race: the base EventListener constructor walks
                // the existing source list before our ctor body assigns _health.
                // Queue and enable in Initialize.
                _pendingSources.Add(eventSource);
                return;
            }
        }
        EnableEvents(eventSource, EventLevel.Warning);
    }

    /// <inheritdoc />
    protected override void OnEventWritten(EventWrittenEventArgs eventData)
    {
        if (eventData is null) return;
        if (eventData.EventSource?.Name != OtlpExporterEventSourceName) return;
        // Warning is numerically larger than Error/Critical in EventLevel —
        // smaller value ⇒ more severe. Anything at or more severe than
        // Warning is a failure signal we surface.
        if (eventData.Level > EventLevel.Warning) return;

        var health = Volatile.Read(ref _health);
        if (health is null) return;

        Interlocked.Exchange(ref _lastFailureUtcTicks, DateTimeOffset.UtcNow.UtcTicks);
        health.RecordFailure(FormatReason(eventData));
    }

    /// <summary>
    /// Format a Warning/Error/Critical event as a short human-readable reason
    /// string suitable for the settings status line. Falls back to a generic
    /// <c>"OTLP exporter event N"</c> when the SDK omits an event name —
    /// better noise than silence.
    /// </summary>
    private static string FormatReason(EventWrittenEventArgs e)
    {
        var name = string.IsNullOrEmpty(e.EventName) ? "OTLP exporter event" : e.EventName!;
        if (e.Payload is null || e.Payload.Count == 0)
        {
            return string.Format(CultureInfo.InvariantCulture, "{0} (id={1})", name, e.EventId);
        }
        // Stringify each payload value defensively — payload entries can be
        // null, boxed primitives, or object references; ToString() copes.
        var payload = string.Join(" ", e.Payload.Select(p => p?.ToString() ?? "<null>"));
        return string.Format(CultureInfo.InvariantCulture, "{0} (id={1}): {2}", name, e.EventId, payload);
    }

    /// <summary>
    /// Test-only entrypoint to drive the success-synthesis tick deterministically.
    /// Returns the same decision the background timer would have made.
    /// </summary>
    /// <returns>
    /// <c>true</c> when the tick decided no failure had been recorded within
    /// the current success window and called
    /// <see cref="ExporterHealthMonitor.RecordSuccess"/>;
    /// <c>false</c> when a recent failure suppressed the success pulse.
    /// </returns>
    internal bool TickSuccessForTests() => SafeTickSuccess();

    private bool SafeTickSuccess()
    {
        var health = Volatile.Read(ref _health);
        if (health is null) return false;

        var lastFailureTicks = Interlocked.Read(ref _lastFailureUtcTicks);
        if (lastFailureTicks != 0
            && DateTimeOffset.UtcNow.UtcTicks - lastFailureTicks <= _successInterval.Ticks)
        {
            return false;
        }
        health.RecordSuccess();
        return true;
    }

    /// <inheritdoc />
    public override void Dispose()
    {
        _successTimer?.Dispose();
        base.Dispose();
    }
}
