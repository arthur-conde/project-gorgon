using Microsoft.Extensions.Logging;
using System.Diagnostics;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using Mithril.Shared.Character;
using Mithril.Shared.Diagnostics.Telemetry;
using Mithril.Shared.Modules;
using Microsoft.Extensions.Hosting;

namespace Mithril.Shared.Diagnostics.Performance;

/// <summary>
/// Owns the WPF-side perf instrumentation: <see cref="CompositionTarget.Rendering"/>,
/// <see cref="DispatcherHooks"/>, <see cref="InputManager"/>, binding-error
/// listener, GC polling, and the 1Hz counter timer. Subscribes only while a
/// session is active so the disabled state pays nothing.
///
/// The <see cref="Toggle"/> method is the entry point the hotkey calls; it
/// composes the <see cref="SessionHeader"/>, hands off to
/// <see cref="IPerfRecorder"/>, then wires the hooks on the UI dispatcher.
/// While attached, hooks emit via <see cref="MithrilActivitySources"/> +
/// <see cref="MithrilMeters"/> — the recorder's file exporter listens and
/// writes the JSON-lines schema.
/// </summary>
public sealed class PerfRecorderHostedService : IHostedService, IDisposable
{
    private readonly IPerfRecorder _recorder;
    private readonly ILogger? _logger;
    private readonly IActiveCharacterService _activeChar;
    private readonly IReadOnlyList<IMithrilModule> _modules;
    private readonly Func<bool> _verboseFrameEvents;

    private readonly object _lock = new();
    private Dispatcher? _uiDispatcher;
    private bool _hooksAttached;

    // Frame timing
    private long _lastFrameTicks;
    private const double StallThresholdMs = FrameStats.DefaultStallThresholdMs;

    // Dispatcher queue tracking
    private int _queueDepth;
    private readonly Dictionary<DispatcherOperation, (long startTicks, int depthAtStart, Activity? activity)> _opStarts = new();

    // Rolling 200ms window of recent dispatcher op completions, used to attribute
    // stalls. Both Rendering and OperationCompleted run on the UI dispatcher so a
    // plain Queue is safe without locking — no cross-thread interleaving.
    private readonly Queue<(long ticks, double runMs)> _recentOps = new();
    private static readonly long StallWindowTicks =
        (long)(Stopwatch.Frequency * (StallAttribution.WindowMs / 1000.0));

    // Input latency: open an activity on PreProcessInput, close on next Rendering
    private Activity? _pendingInputActivity;

    // GC polling
    private int _lastGen0;
    private int _lastGen1;
    private int _lastGen2;

    // 1Hz counter + GC timer (one timer drives both)
    private System.Threading.Timer? _counterTimer;
    private BindingErrorTraceListener? _bindingListener;

    public PerfRecorderHostedService(
        IPerfRecorder recorder,
        IActiveCharacterService activeChar,
        IEnumerable<IMithrilModule> modules,
        Func<bool> verboseFrameEvents,
        ILogger? logger = null)
    {
        _recorder = recorder;
        _activeChar = activeChar;
        _modules = modules.ToList();
        _verboseFrameEvents = verboseFrameEvents;
        _logger = logger;
    }

    public Task StartAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    public Task StopAsync(CancellationToken cancellationToken)
    {
        try { StopInternal(); } catch (Exception ex) { _logger?.LogWarning(ex, "Stop on shutdown failed"); }
        return Task.CompletedTask;
    }

    /// <summary>True if currently recording. Used by the hotkey to label the toast.</summary>
    public bool IsActive => _recorder.IsActive;

    /// <summary>Flip recording state. Always called from the UI dispatcher (hotkey marshals there).</summary>
    public void Toggle()
    {
        if (_recorder.IsActive) StopInternal();
        else StartInternal();
    }

    private void StartInternal()
    {
        try
        {
            _uiDispatcher = Application.Current?.Dispatcher;
            if (_uiDispatcher is null)
            {
                _logger?.LogWarning("Cannot start: no WPF Application yet.");
                return;
            }

            var header = BuildSessionHeader();
            _recorder.Start(header);
            if (!_recorder.IsActive) return; // session start failed; recorder already logged

            // Hooks must be wired on the UI thread because CompositionTarget /
            // DispatcherHooks bind to the calling thread's dispatcher.
            if (_uiDispatcher.CheckAccess()) AttachHooks();
            else _uiDispatcher.Invoke(AttachHooks);
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "StartInternal failed");
            try { _recorder.Stop(); } catch { }
        }
    }

    private void StopInternal()
    {
        try
        {
            var d = _uiDispatcher;
            if (d is not null && _hooksAttached)
            {
                if (d.CheckAccess()) DetachHooks();
                else d.Invoke(DetachHooks);
            }
        }
        catch (Exception ex) { _logger?.LogWarning(ex, "DetachHooks failed"); }

        try { _recorder.Stop(); }
        catch (Exception ex) { _logger?.LogWarning(ex, "Stop failed"); }
    }

    // ── Hook lifecycle (UI thread) ─────────────────────────────────────────

    private void AttachHooks()
    {
        lock (_lock)
        {
            if (_hooksAttached || _uiDispatcher is null) return;

            CompositionTarget.Rendering += OnRendering;
            _uiDispatcher.Hooks.OperationStarted += OnDispatcherOperationStarted;
            _uiDispatcher.Hooks.OperationCompleted += OnDispatcherOperationCompleted;
            InputManager.Current.PreProcessInput += OnPreProcessInput;

            _bindingListener = new BindingErrorTraceListener();
            PresentationTraceSources.DataBindingSource.Listeners.Add(_bindingListener);
            PresentationTraceSources.DataBindingSource.Switch.Level = SourceLevels.Error;

            _lastGen0 = GC.CollectionCount(0);
            _lastGen1 = GC.CollectionCount(1);
            _lastGen2 = GC.CollectionCount(2);
            _lastFrameTicks = 0;

            _counterTimer = new System.Threading.Timer(OnCounterTick, null,
                TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(1));

            _hooksAttached = true;
        }
    }

    private void DetachHooks()
    {
        lock (_lock)
        {
            if (!_hooksAttached) return;

            try { CompositionTarget.Rendering -= OnRendering; } catch { }
            if (_uiDispatcher is not null)
            {
                try { _uiDispatcher.Hooks.OperationStarted -= OnDispatcherOperationStarted; } catch { }
                try { _uiDispatcher.Hooks.OperationCompleted -= OnDispatcherOperationCompleted; } catch { }
            }
            try { InputManager.Current.PreProcessInput -= OnPreProcessInput; } catch { }

            if (_bindingListener is not null)
            {
                try { PresentationTraceSources.DataBindingSource.Listeners.Remove(_bindingListener); } catch { }
                _bindingListener = null;
            }

            try { _counterTimer?.Dispose(); } catch { }
            _counterTimer = null;

            // Close any in-flight dispatcher activities so we don't leak Activity instances.
            foreach (var kv in _opStarts) { try { kv.Value.activity?.Dispose(); } catch { } }
            _opStarts.Clear();
            _recentOps.Clear();
            _queueDepth = 0;
            try { _pendingInputActivity?.Dispose(); } catch { }
            _pendingInputActivity = null;

            _hooksAttached = false;
        }
    }

    // ── CompositionTarget.Rendering ───────────────────────────────────────

    private void OnRendering(object? sender, EventArgs e)
    {
        var now = Stopwatch.GetTimestamp();
        if (_lastFrameTicks != 0)
        {
            var intervalMs = (now - _lastFrameTicks) * 1000.0 / Stopwatch.Frequency;
            var stall = intervalMs > StallThresholdMs;

            // Input → render latency. Closed on the first frame after the input
            // was stamped; the activity's Duration is the latency.
            if (_pendingInputActivity is not null)
            {
                try { _pendingInputActivity.Dispose(); } catch { }
                _pendingInputActivity = null;
            }

            // Always feed the frame-interval histogram; the exporter windows it
            // into a frame_summary record each second.
            MithrilMeters.Wpf.FrameIntervalMs.Record(intervalMs,
                new KeyValuePair<string, object?>("stall", stall));

            // Individual frame records: only on stall OR verbose mode, to keep the
            // file small. The exporter maps these to {stall} or {frame} kinds based
            // on the tag value.
            if (stall || _verboseFrameEvents())
            {
                using var act = MithrilActivitySources.Wpf.StartActivity("frame");
                if (act is not null)
                {
                    act.SetTag("interval_ms", intervalMs);
                    act.SetTag("stall", stall);
                    act.SetTag("op", "");
                    if (stall) act.SetTag("attribution",
                        StallAttribution.Classify(SumRecentOpRunMs(now)));
                }
            }
        }
        _lastFrameTicks = now;
    }

    // ── Dispatcher hooks ──────────────────────────────────────────────────

    private void OnDispatcherOperationStarted(object? sender, DispatcherHookEventArgs e)
    {
        // OperationStarted fires when the operation begins executing — at that
        // point queue depth is "ops still pending behind me" + me. Track simple
        // in-flight count via Started/Completed deltas; not exact for priority
        // ordering but a good proxy.
        Interlocked.Increment(ref _queueDepth);
        var depthAtStart = _queueDepth;
        var activity = MithrilActivitySources.Wpf.StartActivity("dispatcher");
        if (activity is not null)
        {
            activity.SetTag("priority", e.Operation.Priority.ToString());
            activity.SetTag("wait_ms", 0.0);
            activity.SetTag("depth", (long)depthAtStart);
        }
        _opStarts[e.Operation] = (Stopwatch.GetTimestamp(), depthAtStart, activity);
    }

    private void OnDispatcherOperationCompleted(object? sender, DispatcherHookEventArgs e)
    {
        if (!_opStarts.Remove(e.Operation, out var start)) return;
        Interlocked.Decrement(ref _queueDepth);

        var now = Stopwatch.GetTimestamp();
        var runMs = (now - start.startTicks) * 1000.0 / Stopwatch.Frequency;

        // Feed the stall-attribution ring. Both the producer (this method) and
        // the consumer (OnRendering) run on the UI dispatcher — no lock needed.
        _recentOps.Enqueue((now, runMs));
        var cutoff = now - StallWindowTicks;
        while (_recentOps.Count > 0 && _recentOps.Peek().ticks < cutoff)
            _recentOps.Dequeue();

        // Stopping the activity captures run_ms as Activity.Duration.
        try { start.activity?.Dispose(); } catch { }
    }

    /// <summary>
    /// Sum the <c>runMs</c> of dispatcher ops that completed within the last
    /// <see cref="StallAttribution.WindowMs"/> milliseconds. Called from the
    /// stall emit path to decide whether the UI thread was busy during the
    /// bad frame interval.
    /// </summary>
    private double SumRecentOpRunMs(long nowTicks)
    {
        var cutoff = nowTicks - StallWindowTicks;
        double sum = 0;
        foreach (var (ticks, runMs) in _recentOps)
            if (ticks >= cutoff) sum += runMs;
        return sum;
    }

    // ── Input ─────────────────────────────────────────────────────────────

    private void OnPreProcessInput(object? sender, PreProcessInputEventArgs e)
    {
        var input = e.StagingItem?.Input;
        if (input is null) return;
        // Only first stamp per "frame window" — multiple input events in the
        // same render tick coalesce to one latency measurement.
        if (_pendingInputActivity is not null) return;

        string kind;
        if (input is MouseEventArgs) kind = "mouse";
        else if (input is KeyEventArgs) kind = "key";
        else return; // ignore stylus/touch/tablet for now

        var act = MithrilActivitySources.Wpf.StartActivity("input_latency");
        act?.SetTag("kind", kind);
        _pendingInputActivity = act;
    }

    // ── 1Hz counters + GC ─────────────────────────────────────────────────

    private void OnCounterTick(object? _)
    {
        try
        {
            var gen0 = GC.CollectionCount(0);
            var gen1 = GC.CollectionCount(1);
            var gen2 = GC.CollectionCount(2);
            if (gen2 > _lastGen2) EmitGc(2);
            else if (gen1 > _lastGen1) EmitGc(1);
            else if (gen0 > _lastGen0) EmitGc(0);
            _lastGen0 = gen0;
            _lastGen1 = gen1;
            _lastGen2 = gen2;

            using var proc = Process.GetCurrentProcess();
            var wsMb = proc.WorkingSet64 / (1024L * 1024L);
            using var act = MithrilActivitySources.Wpf.StartActivity("counter");
            if (act is not null)
            {
                act.SetTag("working_set_mb", wsMb);
                act.SetTag("gen0", (long)gen0);
                act.SetTag("gen1", (long)gen1);
                act.SetTag("gen2", (long)gen2);
                act.SetTag("threads", (long)proc.Threads.Count);
                act.SetTag("handles", (long)proc.HandleCount);
                act.SetTag("queue_depth", (long)Volatile.Read(ref _queueDepth));
            }
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "Counter tick failed");
        }
    }

    private static void EmitGc(int generation)
    {
        using var act = MithrilActivitySources.Wpf.StartActivity("gc");
        if (act is not null)
        {
            act.SetTag("generation", (long)generation);
            act.SetTag("duration_ms", 0.0);
        }
    }

    // ── Session header ────────────────────────────────────────────────────

    private SessionHeader BuildSessionHeader()
    {
        var build = Assembly.GetEntryAssembly()?
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "?";
        var os = RuntimeInformation.OSDescription;

        var dpi = 96.0;
        var win = Application.Current?.MainWindow;
        if (win is not null)
        {
            try
            {
                var source = PresentationSource.FromVisual(win);
                if (source?.CompositionTarget is not null)
                    dpi = source.CompositionTarget.TransformToDevice.M11 * 96.0;
            }
            catch { }
        }

        // RenderCapability.Tier encodes the tier in the upper 16 bits. 0 = software,
        // 1 = partial hardware accel, 2 = full. Combined with RenderOptions.ProcessRenderMode
        // (Default vs SoftwareOnly) and SystemParameters.IsRemoteSession (TS/RDP forces software),
        // this is enough to disambiguate "stall on a GPU-less machine — expected, any
        // composition load will hitch" from "real transient GPU/driver event worth chasing."
        // Different bugs, different fixes; without this trio every stall-with-idle-dispatcher
        // reads the same.
        var renderTier = System.Windows.Media.RenderCapability.Tier >> 16;
        var renderMode = System.Windows.Media.RenderOptions.ProcessRenderMode.ToString();
        var isRemote = System.Windows.SystemParameters.IsRemoteSession;

        return new SessionHeader(
            Build: build,
            Os: os,
            Gpu: "",                 // TODO: WMI query for adapter description if needed
            RefreshRateHz: 0,        // TODO: EnumDisplaySettings — for now analysts can infer from frame intervals
            Dpi: dpi,
            ActiveCharacter: _activeChar.ActiveCharacterName,
            ActiveServer: _activeChar.ActiveServer,
            LoadedModules: _modules.Select(m => m.Id).ToList(),
            RenderTier: renderTier,
            RenderMode: renderMode,
            IsRemoteSession: isRemote);
    }

    public void Dispose() => StopInternal();
}
