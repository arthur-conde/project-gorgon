using System.Diagnostics;
using System.IO;
using Microsoft.Extensions.Logging;
using Serilog;
using Serilog.Core;
using Serilog.Formatting.Compact;
using MelLogger = Microsoft.Extensions.Logging.ILogger;

namespace Mithril.Shared.Diagnostics.Performance;

/// <summary>
/// Default <see cref="IPerfTracer"/>. Owns a per-session Serilog
/// <see cref="Logger"/> writing to <c>perf-{yyyyMMdd-HHmmss}.jsonl</c> with
/// <see cref="CompactJsonFormatter"/> — same shape as
/// the diagnostics JSON log so analysis tooling (e.g. the
/// <c>mithril-logs</c> MCP server) can consume both streams identically.
///
/// Inactive emit calls take a single uncontended volatile read and return,
/// so production callers can leave <see cref="Scope"/> usages in place
/// without ifdefs.
/// </summary>
public sealed class PerfTracer : IPerfTracer, IDisposable
{
    private readonly string _perfDir;
    private readonly MelLogger? _diagLogger;
    private readonly object _lock = new();
    private Logger? _logger;
    private string? _currentSessionPath;
    private const int RetainedSessionFiles = 30;

    public PerfTracer(string perfDir, MelLogger? logger = null)
    {
        _perfDir = perfDir;
        _diagLogger = logger;
    }

    public bool IsActive => Volatile.Read(ref _logger) is not null;

    public string? CurrentSessionPath => Volatile.Read(ref _currentSessionPath);

    public event EventHandler? IsActiveChanged;

    public void StartSession(SessionHeader header)
    {
        var notify = false;
        lock (_lock)
        {
            if (_logger is not null) return;

            try
            {
                Directory.CreateDirectory(_perfDir);
                PruneOldSessions(_perfDir, RetainedSessionFiles);

                var stamp = DateTimeOffset.UtcNow.ToString("yyyyMMdd-HHmmss");
                var sessionPath = Path.Combine(_perfDir, $"perf-{stamp}.jsonl");

                var logger = new LoggerConfiguration()
                    .MinimumLevel.Verbose()
                    .WriteTo.File(
                        formatter: new CompactJsonFormatter(),
                        path: sessionPath,
                        rollOnFileSizeLimit: true,
                        fileSizeLimitBytes: 50L * 1024 * 1024,
                        retainedFileCountLimit: 5,
                        shared: false,
                        buffered: false,
                        flushToDiskInterval: TimeSpan.FromSeconds(1))
                    .CreateLogger();

                Volatile.Write(ref _logger, logger);
                Volatile.Write(ref _currentSessionPath, sessionPath);
                _diagLogger?.LogInformation($"Session started: {sessionPath}");

                EmitSessionHeader(header);
                notify = true;
            }
            catch (Exception ex)
            {
                _diagLogger?.LogWarning(ex, "Failed to start session");
                _logger = null;
                _currentSessionPath = null;
            }
        }
        if (notify) IsActiveChanged?.Invoke(this, EventArgs.Empty);
    }

    public void StopSession()
    {
        Logger? toDispose;
        string? finishedPath;
        lock (_lock)
        {
            toDispose = _logger;
            finishedPath = _currentSessionPath;
            Volatile.Write(ref _logger, null);
            Volatile.Write(ref _currentSessionPath, null);
        }
        toDispose?.Dispose();
        if (finishedPath is not null)
        {
            _diagLogger?.LogInformation($"Session stopped: {finishedPath}");
            IsActiveChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    public PerfScope Scope(string name, object? tags = null)
    {
        if (!IsActive) return default;
        return new PerfScope(this, name, Stopwatch.GetTimestamp(), tags);
    }

    public void EmitScope(string name, double durationMs, object? tags)
    {
        var log = Volatile.Read(ref _logger);
        if (log is null) return;
        log.Information(
            "{Kind} {Name} {DurationMs:F2}ms {@Tags}",
            PerfEventKinds.Scope, name, durationMs, tags);
    }

    public void EmitFrameSummary(int count, double meanMs, double p50Ms, double p95Ms, double maxMs, int stallCount)
    {
        var log = Volatile.Read(ref _logger);
        if (log is null) return;
        log.Information(
            "{Kind} count={Count} mean={MeanMs:F2} p50={P50Ms:F2} p95={P95Ms:F2} max={MaxMs:F2} stalls={StallCount}",
            PerfEventKinds.FrameSummary, count, meanMs, p50Ms, p95Ms, maxMs, stallCount);
    }

    public void EmitFrame(double intervalMs, bool stall, string? currentOp, string? attribution)
    {
        var log = Volatile.Read(ref _logger);
        if (log is null) return;
        // Two templates so non-stall frames don't carry an empty Attribution slot —
        // verbose-frame mode emits these at ~60 Hz and the field would bloat the file.
        if (stall)
        {
            log.Information(
                "{Kind} interval={IntervalMs:F2}ms stall={Stall} op={CurrentOp} attribution={Attribution}",
                PerfEventKinds.Stall, intervalMs, true, currentOp ?? "", attribution ?? StallAttribution.NonDispatcher);
        }
        else
        {
            log.Information(
                "{Kind} interval={IntervalMs:F2}ms stall={Stall} op={CurrentOp}",
                PerfEventKinds.Frame, intervalMs, false, currentOp ?? "");
        }
    }

    public void EmitDispatcher(string priority, double waitMs, double runMs, int queueDepthAtStart)
    {
        var log = Volatile.Read(ref _logger);
        if (log is null) return;
        log.Information(
            "{Kind} priority={Priority} wait={WaitMs:F2}ms run={RunMs:F2}ms depth={QueueDepthAtStart}",
            PerfEventKinds.Dispatcher, priority, waitMs, runMs, queueDepthAtStart);
    }

    public void EmitCounter(long workingSetMB, int gen0, int gen1, int gen2, int threads, int handles, int dispatcherQueueDepth)
    {
        var log = Volatile.Read(ref _logger);
        if (log is null) return;
        log.Information(
            "{Kind} ws={WorkingSetMB}MB gen0={Gen0} gen1={Gen1} gen2={Gen2} threads={Threads} handles={Handles} q={DispatcherQueueDepth}",
            PerfEventKinds.Counter, workingSetMB, gen0, gen1, gen2, threads, handles, dispatcherQueueDepth);
    }

    public void EmitGc(int generation, double durationMs)
    {
        var log = Volatile.Read(ref _logger);
        if (log is null) return;
        log.Information(
            "{Kind} generation={Generation} duration={DurationMs:F2}ms",
            PerfEventKinds.Gc, generation, durationMs);
    }

    public void EmitBindingError(string message)
    {
        var log = Volatile.Read(ref _logger);
        if (log is null) return;
        log.Warning("{Kind} {Message}", PerfEventKinds.BindingError, message);
    }

    public void EmitInputLatency(string kind, double latencyMs)
    {
        var log = Volatile.Read(ref _logger);
        if (log is null) return;
        log.Information(
            "{Kind} input={InputKind} latency={LatencyMs:F2}ms",
            PerfEventKinds.InputLatency, kind, latencyMs);
    }

    public void EmitModuleActivated(string moduleId, double durationMs)
    {
        var log = Volatile.Read(ref _logger);
        if (log is null) return;
        log.Information(
            "{Kind} module={ModuleId} duration={DurationMs:F2}ms",
            PerfEventKinds.ModuleActivated, moduleId, durationMs);
    }

    public void EmitRefFetch(string file, bool cacheHit, double durationMs, long bytes)
    {
        var log = Volatile.Read(ref _logger);
        if (log is null) return;
        log.Information(
            "{Kind} file={File} cacheHit={CacheHit} duration={DurationMs:F2}ms bytes={Bytes}",
            PerfEventKinds.RefFetch, file, cacheHit, durationMs, bytes);
    }

    private void EmitSessionHeader(SessionHeader header)
    {
        var log = Volatile.Read(ref _logger);
        if (log is null) return;
        log.Information(
            "{Kind} build={Build} os={Os} gpu={Gpu} refresh={RefreshRateHz}Hz dpi={Dpi:F0} character={Character} server={Server} modules={@Modules} renderTier={RenderTier} renderMode={RenderMode} remote={IsRemoteSession}",
            PerfEventKinds.SessionHeader,
            header.Build, header.Os, header.Gpu, header.RefreshRateHz, header.Dpi,
            header.ActiveCharacter ?? "", header.ActiveServer ?? "", header.LoadedModules,
            header.RenderTier, header.RenderMode, header.IsRemoteSession);
    }

    /// <summary>
    /// Keeps the perf directory bounded across sessions. Serilog's
    /// <c>retainedFileCountLimit</c> only prunes within a single rolling
    /// family, not across one-shot session files like ours — so we trim
    /// on each session start instead.
    /// </summary>
    internal static void PruneOldSessions(string perfDir, int retain)
    {
        try
        {
            var files = Directory.EnumerateFiles(perfDir, "perf-*.jsonl")
                .Select(p => new FileInfo(p))
                .OrderByDescending(f => f.CreationTimeUtc)
                .ToList();
            for (var i = retain; i < files.Count; i++)
            {
                try { files[i].Delete(); } catch { /* best-effort */ }
            }
        }
        catch { /* best-effort */ }
    }

    public void Dispose() => StopSession();
}
