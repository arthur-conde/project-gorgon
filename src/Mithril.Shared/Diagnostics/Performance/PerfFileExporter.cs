using System.Collections.Concurrent;
using System.Diagnostics;
using System.Diagnostics.Metrics;
using System.Runtime.InteropServices;
using System.Text;
using Arda.Abstractions.Diagnostics;
using Mithril.Shared.Diagnostics.Telemetry;
using Serilog.Core;

namespace Mithril.Shared.Diagnostics.Performance;

/// <summary>
/// Per-session listener that subscribes to every Mithril <see cref="ActivitySource"/>
/// and <see cref="Meter"/>, maps emits to the JSON-lines schema documented in
/// <c>docs/perf-trace-schema.md</c>, and writes through a Serilog
/// <see cref="Logger"/>. One instance per recording session — disposed on
/// <see cref="IPerfRecorder.Stop"/>.
///
/// All emit paths short-circuit cheaply when no session is active because
/// the listeners simply don't exist outside a session — <see cref="ActivitySource.StartActivity(string)"/>
/// returns null when no listener is attached, and <see cref="Meter"/> instruments
/// don't fan out to disposed listeners.
/// </summary>
internal sealed class PerfFileExporter : IDisposable
{
    private readonly Logger _logger;
    private readonly ActivityListener _activityListener;
    private readonly MeterListener _meterListener;

    private readonly object _frameLock = new();
    private readonly List<double> _frameWindow = new(capacity: 128);
    private DateTime _frameWindowStart = DateTime.UtcNow;
    private System.Threading.Timer? _flushTimer;

    // Counter aggregation: long values summed per (instrument-name + tag-set) over the
    // flush window. Tag-set is the JSON-encoded ordered tag pairs so distinct sets get
    // distinct buckets without holding live KeyValuePair arrays.
    private readonly ConcurrentDictionary<(string Instrument, string TagKey), CounterAccumulator> _counters = new();

    public PerfFileExporter(Logger logger)
    {
        _logger = logger;

        _activityListener = new ActivityListener
        {
            ShouldListenTo = source => source.Name.StartsWith(MithrilActivitySources.Prefix, StringComparison.Ordinal),
            Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllData,
            ActivityStopped = OnActivityStopped,
        };
        ActivitySource.AddActivityListener(_activityListener);

        _meterListener = new MeterListener
        {
            InstrumentPublished = (instrument, listener) =>
            {
                if (instrument.Meter.Name.StartsWith(MithrilMeters.Prefix, StringComparison.Ordinal))
                    listener.EnableMeasurementEvents(instrument);
            },
        };
        _meterListener.SetMeasurementEventCallback<double>(OnDoubleMeasurement);
        _meterListener.SetMeasurementEventCallback<long>(OnLongMeasurement);
        _meterListener.SetMeasurementEventCallback<int>(OnIntMeasurement);
        _meterListener.Start();

        // 1s flush cadence drives both the frame-summary window and counter
        // aggregation. Matches the legacy PerfTracerHostedService.FlushFrameWindow
        // contract so the on-disk cadence is unchanged.
        _flushTimer = new System.Threading.Timer(_ =>
        {
            FlushFrameWindow();
            FlushCounters();
        }, null, TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(1));
    }

    public void EmitSessionHeader(SessionHeader header)
    {
        _logger.Information(
            "{Kind} build={Build} os={Os} gpu={Gpu} refresh={RefreshRateHz}Hz dpi={Dpi:F0} character={Character} server={Server} modules={@Modules} renderTier={RenderTier} renderMode={RenderMode} remote={IsRemoteSession}",
            PerfEventKinds.SessionHeader,
            header.Build, header.Os, header.Gpu, header.RefreshRateHz, header.Dpi,
            header.ActiveCharacter ?? "", header.ActiveServer ?? "", header.LoadedModules,
            header.RenderTier, header.RenderMode, header.IsRemoteSession);
    }

    // ── Activity dispatch ─────────────────────────────────────────────────

    private void OnActivityStopped(Activity activity)
    {
        try
        {
            // Dispatch on (source, operation). One arm per JSON-lines record kind.
            var src = activity.Source.Name;
            var op = activity.OperationName;

            if (src == MithrilActivitySources.ShellModules.Name)
            {
                switch (op)
                {
                    case "activate":
                        _logger.Information(
                            "{Kind} module={ModuleId} duration={DurationMs:F2}ms",
                            PerfEventKinds.ModuleActivated,
                            GetString(activity, "module.id"),
                            activity.Duration.TotalMilliseconds);
                        return;
                    case "gate.open":
                        _logger.Information(
                            "{Kind} module={ModuleId} duration={DurationMs:F2}ms",
                            PerfEventKinds.GateOpen,
                            GetString(activity, "module.id"),
                            activity.Duration.TotalMilliseconds);
                        return;
                    case "view.resolve":
                        _logger.Information(
                            "{Kind} module={ModuleId} duration={DurationMs:F2}ms",
                            PerfEventKinds.ViewResolve,
                            GetString(activity, "module.id"),
                            activity.Duration.TotalMilliseconds);
                        return;
                    case "discover":
                        _logger.Information(
                            "{Kind} count={DiscoveredCount} duration={DurationMs:F2}ms",
                            PerfEventKinds.ModuleDiscover,
                            (int)GetLong(activity, "discovered_count"),
                            activity.Duration.TotalMilliseconds);
                        return;
                }
            }

            if (src == MithrilActivitySources.Reference.Name && op == "fetch")
            {
                // Outcome is set by the producer; for legacy CDN-only paths it defaults
                // to "cdn", matching the pre-PR-B record shape (cacheHit + bytes only).
                _logger.Information(
                    "{Kind} file={File} cacheHit={CacheHit} outcome={Outcome} duration={DurationMs:F2}ms bytes={Bytes}",
                    PerfEventKinds.RefFetch,
                    GetString(activity, "file"),
                    GetBool(activity, "cache_hit"),
                    GetStringOrDefault(activity, "outcome", "cdn"),
                    activity.Duration.TotalMilliseconds,
                    GetLong(activity, "bytes"));
                return;
            }

            if (src == ArdaActivitySources.Ingest.Name && op == "batch.process")
            {
                _logger.Information(
                    "{Kind} source={Source} lines={LineCount} classified={ClassifiedCount} duration={DurationMs:F2}ms",
                    PerfEventKinds.ArdaBatch,
                    GetString(activity, "source"),
                    (int)GetLong(activity, "line_count"),
                    (int)GetLong(activity, "classified_count"),
                    activity.Duration.TotalMilliseconds);
                return;
            }

            if (src == ArdaActivitySources.Dispatch.Name)
            {
                switch (op)
                {
                    case "world_driver":
                        _logger.Information(
                            "{Kind} source={SourceFamily} lines={LineCount} halted={Halted} duration={DurationMs:F2}ms",
                            PerfEventKinds.ArdaWorldDriver,
                            GetString(activity, "source.family"),
                            GetLong(activity, "line_count"),
                            GetBool(activity, "halted"),
                            activity.Duration.TotalMilliseconds);
                        return;
                    case "dispatch_verb":
                        _logger.Information(
                            "{Kind} verb={Verb} handlers={HandlerCount} duration={DurationMs:F2}ms",
                            PerfEventKinds.ArdaDispatch,
                            GetString(activity, "verb"),
                            (int)GetLong(activity, "handler.count"),
                            activity.Duration.TotalMilliseconds);
                        return;
                }
            }

            if (src == ArdaActivitySources.Composition.Name && op.StartsWith("compose.", StringComparison.Ordinal))
            {
                _logger.Information(
                    "{Kind} composer={Composer} event={EventType} duration={DurationMs:F2}ms",
                    PerfEventKinds.ArdaCompose,
                    op["compose.".Length..],
                    GetString(activity, "event"),
                    activity.Duration.TotalMilliseconds);
                return;
            }

            if (src == MithrilActivitySources.Wpf.Name)
            {
                switch (op)
                {
                    case "dispatcher":
                        _logger.Information(
                            "{Kind} priority={Priority} wait={WaitMs:F2}ms run={RunMs:F2}ms depth={QueueDepthAtStart}",
                            PerfEventKinds.Dispatcher,
                            GetString(activity, "priority"),
                            GetDouble(activity, "wait_ms"),
                            activity.Duration.TotalMilliseconds,
                            (int)GetLong(activity, "depth"));
                        return;
                    case "input_latency":
                        _logger.Information(
                            "{Kind} input={InputKind} latency={LatencyMs:F2}ms",
                            PerfEventKinds.InputLatency,
                            GetString(activity, "kind"),
                            activity.Duration.TotalMilliseconds);
                        return;
                    case "binding_error":
                        _logger.Warning(
                            "{Kind} {Message}",
                            PerfEventKinds.BindingError,
                            GetString(activity, "message"));
                        return;
                    case "frame":
                        // Per-frame record. The producer only starts this Activity when
                        // (stall OR verbose) is true so the file isn't spammed at 60 Hz.
                        var stall = GetBool(activity, "stall");
                        var intervalMs = GetDouble(activity, "interval_ms");
                        var currentOp = GetString(activity, "op");
                        if (stall)
                        {
                            _logger.Information(
                                "{Kind} interval={IntervalMs:F2}ms stall={Stall} op={CurrentOp} attribution={Attribution}",
                                PerfEventKinds.Stall, intervalMs, true, currentOp,
                                GetString(activity, "attribution"));
                        }
                        else
                        {
                            _logger.Information(
                                "{Kind} interval={IntervalMs:F2}ms stall={Stall} op={CurrentOp}",
                                PerfEventKinds.Frame, intervalMs, false, currentOp);
                        }
                        return;
                    case "counter":
                        _logger.Information(
                            "{Kind} ws={WorkingSetMB}MB gen0={Gen0} gen1={Gen1} gen2={Gen2} threads={Threads} handles={Handles} q={DispatcherQueueDepth}",
                            PerfEventKinds.Counter,
                            GetLong(activity, "working_set_mb"),
                            (int)GetLong(activity, "gen0"),
                            (int)GetLong(activity, "gen1"),
                            (int)GetLong(activity, "gen2"),
                            (int)GetLong(activity, "threads"),
                            (int)GetLong(activity, "handles"),
                            (int)GetLong(activity, "queue_depth"));
                        return;
                    case "gc":
                        _logger.Information(
                            "{Kind} generation={Generation} duration={DurationMs:F2}ms",
                            PerfEventKinds.Gc,
                            (int)GetLong(activity, "generation"),
                            GetDouble(activity, "duration_ms"));
                        return;
                }
            }

            // Fallback: any other Mithril ActivitySource gets a generic 'scope' record so
            // ad-hoc producers still leave a trace (matches PerfEventKinds.Scope contract).
            _logger.Information(
                "{Kind} {Name} {DurationMs:F2}ms {@Tags}",
                PerfEventKinds.Scope,
                $"{src}/{op}",
                activity.Duration.TotalMilliseconds,
                activity.TagObjects);
        }
        catch
        {
            // Exporter never throws into the producer's stop path.
        }
    }

    // ── Meter dispatch ────────────────────────────────────────────────────

    private void OnDoubleMeasurement(Instrument instrument, double value,
        ReadOnlySpan<KeyValuePair<string, object?>> tags, object? state)
    {
        // Per-frame interval feeds the windowed summary only. Individual frame
        // records come through the 'frame' Activity, not the histogram.
        if (instrument.Name == "mithril.wpf.frame.interval_ms")
        {
            lock (_frameLock) _frameWindow.Add(value);
            return;
        }
        // Histograms other than frame-interval pass through as counter samples for now;
        // there's no consumer for the percentile shape until #815 lands a percentile-aware
        // exporter. Sum as long ms so the file shape stays uniform.
        AccumulateCounter(instrument.Name, tags, (long)value);
    }

    private void OnLongMeasurement(Instrument instrument, long value,
        ReadOnlySpan<KeyValuePair<string, object?>> tags, object? state)
    {
        AccumulateCounter(instrument.Name, tags, value);
    }

    private void OnIntMeasurement(Instrument instrument, int value,
        ReadOnlySpan<KeyValuePair<string, object?>> tags, object? state)
    {
        AccumulateCounter(instrument.Name, tags, value);
    }

    private void AccumulateCounter(string instrumentName, ReadOnlySpan<KeyValuePair<string, object?>> tags, long value)
    {
        var key = (instrumentName, TagKey(tags));
        // `tags` is a ReadOnlySpan and so can't be captured by a lambda — eagerly
        // allocate the bucket on first sighting, then race-add via GetOrAdd.
        if (!_counters.TryGetValue(key, out var bucket))
        {
            bucket = _counters.GetOrAdd(key, new CounterAccumulator(tags));
        }
        Interlocked.Add(ref bucket.Sum, value);
    }

    private void FlushFrameWindow()
    {
        FrameStats.Summary summary;
        lock (_frameLock)
        {
            if (_frameWindow.Count == 0)
            {
                _frameWindowStart = DateTime.UtcNow;
                return;
            }
            summary = FrameStats.Compute(CollectionsMarshal.AsSpan(_frameWindow));
            _frameWindow.Clear();
            _frameWindowStart = DateTime.UtcNow;
        }
        try
        {
            _logger.Information(
                "{Kind} count={Count} mean={MeanMs:F2} p50={P50Ms:F2} p95={P95Ms:F2} max={MaxMs:F2} stalls={StallCount}",
                PerfEventKinds.FrameSummary, summary.Count, summary.MeanMs,
                summary.P50Ms, summary.P95Ms, summary.MaxMs, summary.StallCount);
        }
        catch { }
    }

    private void FlushCounters()
    {
        // Drain each bucket in turn. We don't snapshot-and-clear atomically across all
        // buckets; concurrent increments during the drain land in the next window.
        foreach (var key in _counters.Keys.ToArray())
        {
            if (!_counters.TryGetValue(key, out var bucket)) continue;
            var sum = Interlocked.Exchange(ref bucket.Sum, 0);
            if (sum == 0) continue;
            try
            {
                _logger.Information(
                    "{Kind} instrument={Instrument} sum={Sum} {@Tags}",
                    PerfEventKinds.MeterCounter, key.Instrument, sum, bucket.Tags);
            }
            catch { }
        }
    }

    public void Dispose()
    {
        try { _flushTimer?.Dispose(); } catch { }
        _flushTimer = null;
        try { FlushFrameWindow(); } catch { }
        try { FlushCounters(); } catch { }
        try { _activityListener.Dispose(); } catch { }
        try { _meterListener.Dispose(); } catch { }
    }

    // ── Tag helpers ───────────────────────────────────────────────────────

    private static string GetString(Activity activity, string key)
    {
        foreach (var t in activity.TagObjects)
            if (t.Key == key) return t.Value?.ToString() ?? "";
        return "";
    }

    private static string GetStringOrDefault(Activity activity, string key, string fallback)
    {
        foreach (var t in activity.TagObjects)
            if (t.Key == key) return t.Value?.ToString() ?? fallback;
        return fallback;
    }

    private static bool GetBool(Activity activity, string key)
    {
        foreach (var t in activity.TagObjects)
            if (t.Key == key) return t.Value is bool b && b;
        return false;
    }

    private static long GetLong(Activity activity, string key)
    {
        foreach (var t in activity.TagObjects)
            if (t.Key == key) return Convert.ToInt64(t.Value ?? 0L);
        return 0L;
    }

    private static double GetDouble(Activity activity, string key)
    {
        foreach (var t in activity.TagObjects)
            if (t.Key == key) return Convert.ToDouble(t.Value ?? 0.0);
        return 0.0;
    }

    private static string TagKey(ReadOnlySpan<KeyValuePair<string, object?>> tags)
    {
        if (tags.IsEmpty) return string.Empty;
        var sb = new StringBuilder(64);
        for (var i = 0; i < tags.Length; i++)
        {
            if (i > 0) sb.Append(',');
            sb.Append(tags[i].Key).Append('=').Append(tags[i].Value);
        }
        return sb.ToString();
    }

    private sealed class CounterAccumulator
    {
        public long Sum;
        public readonly Dictionary<string, object?> Tags;
        public CounterAccumulator(ReadOnlySpan<KeyValuePair<string, object?>> tags)
        {
            Tags = new Dictionary<string, object?>(tags.Length);
            foreach (var t in tags) Tags[t.Key] = t.Value;
        }
    }
}
