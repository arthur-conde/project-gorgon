using System.Diagnostics;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using Mithril.Shared.Character;
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
/// <see cref="IPerfTracer"/>, then wires the hooks on the UI dispatcher.
/// </summary>
public sealed class PerfTracerHostedService : IHostedService, IDisposable
{
    private readonly IPerfTracer _tracer;
    private readonly IDiagnosticsSink? _diag;
    private readonly IActiveCharacterService _activeChar;
    private readonly IReadOnlyList<IMithrilModule> _modules;
    private readonly Func<bool> _verboseFrameEvents;

    private readonly object _lock = new();
    private Dispatcher? _uiDispatcher;
    private bool _hooksAttached;

    // Frame timing
    private long _lastFrameTicks;
    private readonly List<double> _frameWindow = new(capacity: 128);
    private DateTime _frameWindowStart;
    private const double StallThresholdMs = FrameStats.DefaultStallThresholdMs;

    // Dispatcher queue tracking
    private int _queueDepth;
    private readonly Dictionary<DispatcherOperation, (long startTicks, int depthAtStart)> _opStarts = new();

    // Input latency: stamp on PreProcessInput, close on next Rendering
    private long _pendingInputTicks;
    private string? _pendingInputKind;

    // GC polling
    private int _lastGen0;
    private int _lastGen1;
    private int _lastGen2;

    // 1Hz counter + GC timer (one timer drives both)
    private System.Threading.Timer? _counterTimer;
    private BindingErrorTraceListener? _bindingListener;

    public PerfTracerHostedService(
        IPerfTracer tracer,
        IActiveCharacterService activeChar,
        IEnumerable<IMithrilModule> modules,
        Func<bool> verboseFrameEvents,
        IDiagnosticsSink? diag = null)
    {
        _tracer = tracer;
        _activeChar = activeChar;
        _modules = modules.ToList();
        _verboseFrameEvents = verboseFrameEvents;
        _diag = diag;
    }

    public Task StartAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    public Task StopAsync(CancellationToken cancellationToken)
    {
        try { StopInternal(); } catch (Exception ex) { _diag?.Warn("PerfTrace", $"Stop on shutdown failed: {ex.Message}"); }
        return Task.CompletedTask;
    }

    /// <summary>True if currently recording. Used by the hotkey to label the toast.</summary>
    public bool IsActive => _tracer.IsActive;

    /// <summary>Flip recording state. Always called from the UI dispatcher (hotkey marshals there).</summary>
    public void Toggle()
    {
        if (_tracer.IsActive) StopInternal();
        else StartInternal();
    }

    private void StartInternal()
    {
        try
        {
            _uiDispatcher = Application.Current?.Dispatcher;
            if (_uiDispatcher is null)
            {
                _diag?.Warn("PerfTrace", "Cannot start: no WPF Application yet.");
                return;
            }

            var header = BuildSessionHeader();
            _tracer.StartSession(header);
            if (!_tracer.IsActive) return; // session start failed; tracer already logged

            // Hooks must be wired on the UI thread because CompositionTarget /
            // DispatcherHooks bind to the calling thread's dispatcher.
            if (_uiDispatcher.CheckAccess()) AttachHooks();
            else _uiDispatcher.Invoke(AttachHooks);
        }
        catch (Exception ex)
        {
            _diag?.Warn("PerfTrace", $"StartInternal failed: {ex.Message}");
            try { _tracer.StopSession(); } catch { }
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
        catch (Exception ex) { _diag?.Warn("PerfTrace", $"DetachHooks failed: {ex.Message}"); }

        try { _tracer.StopSession(); }
        catch (Exception ex) { _diag?.Warn("PerfTrace", $"StopSession failed: {ex.Message}"); }
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

            _bindingListener = new BindingErrorTraceListener(_tracer);
            PresentationTraceSources.DataBindingSource.Listeners.Add(_bindingListener);
            PresentationTraceSources.DataBindingSource.Switch.Level = SourceLevels.Error;

            _lastGen0 = GC.CollectionCount(0);
            _lastGen1 = GC.CollectionCount(1);
            _lastGen2 = GC.CollectionCount(2);
            _frameWindowStart = DateTime.UtcNow;
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

            // Flush any pending frame-window aggregate
            FlushFrameWindow();

            _opStarts.Clear();
            _queueDepth = 0;
            _pendingInputTicks = 0;
            _pendingInputKind = null;

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
            // was stamped, then cleared so a long render doesn't get charged to
            // the next input.
            if (_pendingInputTicks != 0)
            {
                var latencyMs = (now - _pendingInputTicks) * 1000.0 / Stopwatch.Frequency;
                _tracer.EmitInputLatency(_pendingInputKind ?? "unknown", latencyMs);
                _pendingInputTicks = 0;
                _pendingInputKind = null;
            }

            if (stall)
            {
                _tracer.EmitFrame(intervalMs, stall: true, currentOp: null);
            }
            else if (_verboseFrameEvents())
            {
                _tracer.EmitFrame(intervalMs, stall: false, currentOp: null);
            }

            _frameWindow.Add(intervalMs);
            if (DateTime.UtcNow - _frameWindowStart >= TimeSpan.FromSeconds(1))
                FlushFrameWindow();
        }
        _lastFrameTicks = now;
    }

    private void FlushFrameWindow()
    {
        if (_frameWindow.Count == 0)
        {
            _frameWindowStart = DateTime.UtcNow;
            return;
        }
        var summary = FrameStats.Compute(System.Runtime.InteropServices.CollectionsMarshal.AsSpan(_frameWindow));
        _tracer.EmitFrameSummary(summary.Count, summary.MeanMs, summary.P50Ms, summary.P95Ms, summary.MaxMs, summary.StallCount);
        _frameWindow.Clear();
        _frameWindowStart = DateTime.UtcNow;
    }

    // ── Dispatcher hooks ──────────────────────────────────────────────────

    private void OnDispatcherOperationStarted(object? sender, DispatcherHookEventArgs e)
    {
        // OperationStarted fires when the operation begins executing — at that
        // point queue depth is "ops still pending behind me" + me. Track
        // simple in-flight count via Started/Completed deltas; not exact for
        // priority ordering but a good proxy.
        Interlocked.Increment(ref _queueDepth);
        _opStarts[e.Operation] = (Stopwatch.GetTimestamp(), _queueDepth);
    }

    private void OnDispatcherOperationCompleted(object? sender, DispatcherHookEventArgs e)
    {
        if (!_opStarts.Remove(e.Operation, out var start)) return;
        Interlocked.Decrement(ref _queueDepth);

        var runMs = (Stopwatch.GetTimestamp() - start.startTicks) * 1000.0 / Stopwatch.Frequency;
        // We don't have a "queued at" timestamp from the public hooks API, so
        // wait time is reported as 0 — analysts can infer it from queueDepthAtStart.
        _tracer.EmitDispatcher(
            priority: e.Operation.Priority.ToString(),
            waitMs: 0.0,
            runMs: runMs,
            queueDepthAtStart: start.depthAtStart);
    }

    // ── Input ─────────────────────────────────────────────────────────────

    private void OnPreProcessInput(object? sender, PreProcessInputEventArgs e)
    {
        var input = e.StagingItem?.Input;
        if (input is null) return;
        // Only first stamp per "frame window" — multiple input events in the
        // same render tick coalesce to one latency measurement.
        if (_pendingInputTicks != 0) return;

        string kind;
        if (input is MouseEventArgs) kind = "mouse";
        else if (input is KeyEventArgs) kind = "key";
        else return; // ignore stylus/touch/tablet for now

        _pendingInputTicks = Stopwatch.GetTimestamp();
        _pendingInputKind = kind;
    }

    // ── 1Hz counters + GC ─────────────────────────────────────────────────

    private void OnCounterTick(object? _)
    {
        try
        {
            if (!_tracer.IsActive) return;

            var gen0 = GC.CollectionCount(0);
            var gen1 = GC.CollectionCount(1);
            var gen2 = GC.CollectionCount(2);
            if (gen2 > _lastGen2) _tracer.EmitGc(2, 0.0);
            else if (gen1 > _lastGen1) _tracer.EmitGc(1, 0.0);
            else if (gen0 > _lastGen0) _tracer.EmitGc(0, 0.0);
            _lastGen0 = gen0;
            _lastGen1 = gen1;
            _lastGen2 = gen2;

            using var proc = Process.GetCurrentProcess();
            var wsMb = proc.WorkingSet64 / (1024L * 1024L);
            _tracer.EmitCounter(
                workingSetMB: wsMb,
                gen0: gen0,
                gen1: gen1,
                gen2: gen2,
                threads: proc.Threads.Count,
                handles: proc.HandleCount,
                dispatcherQueueDepth: Volatile.Read(ref _queueDepth));
        }
        catch (Exception ex)
        {
            _diag?.Warn("PerfTrace", $"Counter tick failed: {ex.Message}");
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

        return new SessionHeader(
            Build: build,
            Os: os,
            Gpu: "",                 // TODO: WMI query for adapter description if needed
            RefreshRateHz: 0,        // TODO: EnumDisplaySettings — for now analysts can infer from frame intervals
            Dpi: dpi,
            ActiveCharacter: _activeChar.ActiveCharacterName,
            ActiveServer: _activeChar.ActiveServer,
            LoadedModules: _modules.Select(m => m.Id).ToList());
    }

    public void Dispose() => StopInternal();
}
