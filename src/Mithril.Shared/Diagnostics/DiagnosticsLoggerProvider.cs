using System.Collections.Concurrent;
using System.Reactive.Subjects;
using Microsoft.Extensions.Logging;
using Serilog.Core;
using MelLogLevel = Microsoft.Extensions.Logging.LogLevel;

namespace Mithril.Shared.Diagnostics;

/// <summary>
/// Unified MEL provider: ring buffer + hot Rx stream + Serilog compact-JSON file.
/// </summary>
public sealed class DiagnosticsLoggerProvider : ILoggerProvider, IDiagnosticsLog, IDisposable
{
    /// <summary>Default ring-buffer capacity used by the shell composition.</summary>
    public const int DefaultCapacity = 2000;

    private readonly ConcurrentQueue<DiagnosticEntry> _queue = new();
    private readonly int _capacity;
    private readonly Subject<DiagnosticEntry> _live = new();
    private readonly Logger _fileLogger;
    private readonly object _disposeGate = new();
    private bool _disposed;

    public DiagnosticsLoggerProvider(string logDirectory, int capacity = DefaultCapacity)
        : this(logDirectory, capacity, Array.Empty<ILogEventEnricher>())
    {
    }

    /// <summary>
    /// Constructs the provider with custom Serilog enrichers (e.g. the trace-context
    /// enricher from Mithril.Shared.Telemetry). Mithril.Shared cannot reference the
    /// telemetry assembly directly without creating a cycle, so the caller (typically
    /// the shell composition layer) supplies them.
    /// </summary>
    public DiagnosticsLoggerProvider(string logDirectory, int capacity, params ILogEventEnricher[] enrichers)
    {
        _capacity = capacity;
        Directory.CreateDirectory(logDirectory);
        // Create the file logger BEFORE migrating, so the migration's Publish() calls have a
        // live _fileLogger to write to. (Publish dereferences _fileLogger unconditionally; if
        // migration runs first and emits any Info/Warn — as the boot.log/crash.log sweep does —
        // a null _fileLogger would throw out of this ctor and abort host build.) Migration only
        // touches legacy gorgon-*/boot/crash files, never the live mithril-.json this logger
        // opens, so ordering it after CreateFileLogger is safe.
        _fileLogger = DiagnosticsLogSerilog.CreateFileLogger(logDirectory, enrichers);
        DiagnosticsLogSerilog.MigrateLegacyLogFiles(Publish, logDirectory);
    }

    public IObservable<DiagnosticEntry> Live => _live;

    public IReadOnlyList<DiagnosticEntry> Snapshot() => _queue.ToArray();

    public ILogger CreateLogger(string categoryName) =>
        new DiagnosticsLogger(this, categoryName);

    public void Publish(DiagnosticLevel level, string category, string message)
    {
        if (_disposed) return;

        var entry = new DiagnosticEntry(DateTime.UtcNow, level, category, message);
        _queue.Enqueue(entry);
        while (_queue.Count > _capacity)
            _queue.TryDequeue(out _);

        _live.OnNext(entry);
        _fileLogger.Write(DiagnosticsLogSerilog.Map(level), "{Category} {Message}", category, message);
    }

    internal void Publish(MelLogLevel level, string category, string message) =>
        Publish(DiagnosticsLogSerilog.Map(level), category, message);

    public void Dispose()
    {
        lock (_disposeGate)
        {
            if (_disposed) return;
            _disposed = true;
        }

        _live.OnCompleted();
        _live.Dispose();
        _fileLogger.Dispose();
    }
}
