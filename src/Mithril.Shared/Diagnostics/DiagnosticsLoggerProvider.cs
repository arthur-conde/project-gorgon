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
    private const int DefaultCapacity = 2000;

    private readonly ConcurrentQueue<DiagnosticEntry> _queue = new();
    private readonly int _capacity;
    private readonly Subject<DiagnosticEntry> _live = new();
    private readonly Logger _fileLogger;
    private readonly object _disposeGate = new();
    private bool _disposed;

    public DiagnosticsLoggerProvider(string logDirectory, int capacity = DefaultCapacity)
    {
        _capacity = capacity;
        Directory.CreateDirectory(logDirectory);
        DiagnosticsLogSerilog.MigrateLegacyLogFiles(Publish, logDirectory);
        _fileLogger = DiagnosticsLogSerilog.CreateFileLogger(logDirectory);
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
