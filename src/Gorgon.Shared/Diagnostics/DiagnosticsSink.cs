using System.Collections.Concurrent;

namespace Gorgon.Shared.Diagnostics;

public enum DiagnosticLevel { Trace, Info, Warn, Error }

public sealed record DiagnosticEntry(
    DateTime Timestamp,
    DiagnosticLevel Level,
    string Category,
    string Message);

public interface IDiagnosticsSink
{
    void Write(DiagnosticLevel level, string category, string message);
    IReadOnlyList<DiagnosticEntry> Snapshot();
    event EventHandler<DiagnosticEntry>? EntryAdded;
}

/// <summary>
/// Thread-safe ring-buffer sink. Holds up to <paramref name="capacity"/>
/// entries; older entries are dropped when the buffer fills.
/// </summary>
public sealed class DiagnosticsSink : IDiagnosticsSink
{
    private readonly ConcurrentQueue<DiagnosticEntry> _queue = new();
    private readonly int _capacity;

    public DiagnosticsSink(int capacity = 2000) { _capacity = capacity; }

    public event EventHandler<DiagnosticEntry>? EntryAdded;

    public void Write(DiagnosticLevel level, string category, string message)
    {
        var entry = new DiagnosticEntry(DateTime.UtcNow, level, category, message);
        _queue.Enqueue(entry);
        while (_queue.Count > _capacity) _queue.TryDequeue(out _);
        EntryAdded?.Invoke(this, entry);
    }

    public IReadOnlyList<DiagnosticEntry> Snapshot() => _queue.ToArray();
}

public static class DiagnosticsSinkExtensions
{
    public static void Trace(this IDiagnosticsSink sink, string category, string message)
        => sink.Write(DiagnosticLevel.Trace, category, message);
    public static void Info(this IDiagnosticsSink sink, string category, string message)
        => sink.Write(DiagnosticLevel.Info, category, message);
    public static void Warn(this IDiagnosticsSink sink, string category, string message)
        => sink.Write(DiagnosticLevel.Warn, category, message);
    public static void Error(this IDiagnosticsSink sink, string category, string message)
        => sink.Write(DiagnosticLevel.Error, category, message);
}
