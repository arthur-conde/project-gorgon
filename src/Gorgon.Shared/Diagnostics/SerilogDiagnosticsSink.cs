using System.IO;
using Serilog;
using Serilog.Core;
using Serilog.Events;
using Serilog.Formatting.Compact;

namespace Gorgon.Shared.Diagnostics;

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
        _logger = new LoggerConfiguration()
            .MinimumLevel.Verbose()
            .WriteTo.File(
                formatter: new CompactJsonFormatter(),
                path: Path.Combine(logDirectory, "gorgon-.json"),
                rollingInterval: RollingInterval.Day,
                retainedFileCountLimit: 14,
                shared: false,
                buffered: false)
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
