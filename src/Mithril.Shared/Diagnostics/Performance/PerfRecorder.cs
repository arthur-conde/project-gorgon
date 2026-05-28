using Microsoft.Extensions.Logging;
using Serilog;
using Serilog.Core;
using Serilog.Debugging;
using Serilog.Formatting.Compact;
using MelLogger = Microsoft.Extensions.Logging.ILogger;

namespace Mithril.Shared.Diagnostics.Performance;

/// <summary>
/// Default <see cref="IPerfRecorder"/>. Owns a per-session Serilog
/// <see cref="Logger"/> writing to <c>perf-{yyyyMMdd-HHmmss}.jsonl</c> with
/// <see cref="CompactJsonFormatter"/> — same shape as the diagnostics JSON
/// log so analysis tooling (e.g. the <c>mithril-logs</c> MCP server) can
/// consume both streams identically.
///
/// During a session a <see cref="PerfFileExporter"/> listens to
/// <see cref="System.Diagnostics.ActivitySource"/> + <see cref="System.Diagnostics.Metrics.Meter"/>
/// emits from across the solution and serialises them through the logger
/// in the JSON-lines schema documented in <c>docs/perf-trace-schema.md</c>.
/// </summary>
public sealed class PerfRecorder : IPerfRecorder, IDisposable
{
    private readonly string _perfDir;
    private readonly MelLogger? _diagLogger;
    private readonly object _lock = new();
    private Logger? _logger;
    private PerfFileExporter? _exporter;
    private string? _currentSessionPath;
    private const int RetainedSessionFiles = 30;
    private static int _selfLogWired;

    public PerfRecorder(string perfDir, MelLogger? logger = null)
    {
        _perfDir = perfDir;
        _diagLogger = logger;

        // Serilog's file sink swallows IO errors silently by default. Wire SelfLog
        // once per process to surface "disk full", "file locked by AV", etc. via the
        // diagnostics logger — without this, a perf-trace session can produce an empty
        // .jsonl while IsActive cheerfully reports success.
        if (logger is not null && Interlocked.Exchange(ref _selfLogWired, 1) == 0)
        {
            SelfLog.Enable(msg => logger.LogWarning("Serilog SelfLog: {Message}", msg));
        }
    }

    public bool IsActive => Volatile.Read(ref _logger) is not null;

    public string? CurrentSessionPath => Volatile.Read(ref _currentSessionPath);

    public event EventHandler? IsActiveChanged;

    public void Start(SessionHeader header)
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

                var exporter = new PerfFileExporter(logger, _diagLogger);
                Volatile.Write(ref _logger, logger);
                Volatile.Write(ref _currentSessionPath, sessionPath);
                _exporter = exporter;
                _diagLogger?.LogInformation("Session started: {SessionPath}", sessionPath);

                exporter.EmitSessionHeader(header);
                notify = true;
            }
            catch (Exception ex)
            {
                _diagLogger?.LogWarning(ex, "Failed to start session");
                Volatile.Write(ref _logger, null);
                Volatile.Write(ref _currentSessionPath, null);
                _exporter = null;
            }
        }
        if (notify) IsActiveChanged?.Invoke(this, EventArgs.Empty);
    }

    public void Stop()
    {
        Logger? toDispose;
        PerfFileExporter? exporterToDispose;
        string? finishedPath;
        lock (_lock)
        {
            toDispose = _logger;
            exporterToDispose = _exporter;
            finishedPath = _currentSessionPath;
            Volatile.Write(ref _logger, null);
            Volatile.Write(ref _currentSessionPath, null);
            _exporter = null;
        }
        // Detach listeners FIRST so producers no longer emit through the disposed logger.
        exporterToDispose?.Dispose();
        toDispose?.Dispose();
        if (finishedPath is not null)
        {
            _diagLogger?.LogInformation("Session stopped: {SessionPath}", finishedPath);
            IsActiveChanged?.Invoke(this, EventArgs.Empty);
        }
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

    public void Dispose() => Stop();
}
