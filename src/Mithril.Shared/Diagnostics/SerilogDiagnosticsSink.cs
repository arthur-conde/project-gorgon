using System.IO;
using Serilog;
using Serilog.Core;
using Serilog.Events;
using Serilog.Formatting.Compact;

namespace Mithril.Shared.Diagnostics;

/// <summary>
/// Decorator over <see cref="DiagnosticsSink"/> that also forwards to Serilog,
/// which writes compact-JSON-formatted lines to a daily-rolling file.
/// The inner ring-buffer sink still powers the live <c>DiagnosticsView</c>.
/// </summary>
public sealed class SerilogDiagnosticsSink : IDiagnosticsSink, IDisposable
{
    private readonly IDiagnosticsSink _inner;
    private readonly Logger _logger;

    public SerilogDiagnosticsSink(IDiagnosticsSink inner, string logDirectory)
    {
        _inner = inner;
        Directory.CreateDirectory(logDirectory);
        // Desktop-app rolling policy: roll daily AND on size cap so a single
        // long session can't silently exceed the cap and stop logging (the
        // pre-rollOnFileSizeLimit default behaviour). 50 MB per file × 30
        // retained ≈ 1.5 GB worst-case on disk, which is fine for a user
        // app log directory and gives ~3-5 sessions of ProcessAddItem-heavy
        // verbose history before pruning.
        _logger = new LoggerConfiguration()
            .MinimumLevel.Verbose()
            .WriteTo.File(
                formatter: new CompactJsonFormatter(),
                path: Path.Combine(logDirectory, "mithril-.json"),
                rollingInterval: RollingInterval.Day,
                rollOnFileSizeLimit: true,
                fileSizeLimitBytes: 50L * 1024 * 1024,
                retainedFileCountLimit: 30,
                shared: false,
                buffered: false,
                flushToDiskInterval: TimeSpan.FromSeconds(2))
            .CreateLogger();
    }

    public void Write(DiagnosticLevel level, string category, string message)
    {
        _inner.Write(level, category, message);
        _logger.Write(Map(level), "{Category} {Message}", category, message);
    }

    public IReadOnlyList<DiagnosticEntry> Snapshot() => _inner.Snapshot();

    public event EventHandler<DiagnosticEntry>? EntryAdded
    {
        add => _inner.EntryAdded += value;
        remove => _inner.EntryAdded -= value;
    }

    private static LogEventLevel Map(DiagnosticLevel l) => l switch
    {
        DiagnosticLevel.Trace => LogEventLevel.Verbose,
        DiagnosticLevel.Info  => LogEventLevel.Information,
        DiagnosticLevel.Warn  => LogEventLevel.Warning,
        DiagnosticLevel.Error => LogEventLevel.Error,
        _ => LogEventLevel.Information,
    };

    public void Dispose() => _logger.Dispose();
}
