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
        MigrateLegacyLogFiles(_inner, logDirectory);
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

    /// <summary>
    /// Renames pre-rebrand <c>gorgon-*.json</c> diagnostic files to <c>mithril-*.json</c>.
    /// The shell was renamed from "Gorgon" to "Mithril" in commit 8f91ebf, but the rolling
    /// file path was not updated until this method was added — leaving a generation of
    /// per-day diagnostic files on existing installs under the old prefix. Running once
    /// at sink construction normalises the on-disk state so downstream tools (the
    /// MithrilLogMcp server, ad-hoc analysis) only need to handle a single prefix.
    /// </summary>
    /// <remarks>
    /// Always renames into <c>mithril-{rest}-prebrand.json</c> rather than just
    /// <c>mithril-{rest}.json</c> — there are real installs where both prefixes exist
    /// for the same date, and a direct rename would clash. The <c>-prebrand</c> suffix
    /// is unambiguous, idempotent, and guaranteed not to collide with the active rolling
    /// path (which never produces that suffix). Failures on individual files are logged
    /// to the inner sink and do not break logger construction.
    /// </remarks>
    internal static void MigrateLegacyLogFiles(IDiagnosticsSink diagnostics, string logDirectory)
    {
        IEnumerable<string> legacy;
        try
        {
            legacy = Directory.EnumerateFiles(logDirectory, "gorgon-*.json", SearchOption.TopDirectoryOnly);
        }
        catch (Exception ex)
        {
            diagnostics.Write(DiagnosticLevel.Warn, "SerilogSink",
                $"Could not enumerate legacy log files in {logDirectory}: {ex.Message}");
            return;
        }

        foreach (var src in legacy)
        {
            try
            {
                var fileName = Path.GetFileName(src);
                // "gorgon-".Length == 7
                var rest = fileName.Substring("gorgon-".Length);
                var stem = Path.GetFileNameWithoutExtension(rest);
                var ext = Path.GetExtension(rest);

                var target = ResolveNonClashingTarget(logDirectory, stem, ext);
                File.Move(src, target);
                diagnostics.Write(DiagnosticLevel.Info, "SerilogSink",
                    $"Renamed legacy log file {fileName} -> {Path.GetFileName(target)}");
            }
            catch (Exception ex)
            {
                diagnostics.Write(DiagnosticLevel.Warn, "SerilogSink",
                    $"Failed to migrate legacy log file {src}: {ex.Message}");
            }
        }
    }

    private static string ResolveNonClashingTarget(string logDirectory, string stem, string ext)
    {
        var primary = Path.Combine(logDirectory, $"mithril-{stem}-prebrand{ext}");
        if (!File.Exists(primary)) return primary;

        for (var i = 1; i < 1000; i++)
        {
            var candidate = Path.Combine(logDirectory, $"mithril-{stem}-prebrand_{i:D3}{ext}");
            if (!File.Exists(candidate)) return candidate;
        }
        // Pathological: 1000 prior attempts already exist. Fall back to a timestamp
        // so the rename still succeeds rather than silently dropping the file.
        var ticks = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        return Path.Combine(logDirectory, $"mithril-{stem}-prebrand_{ticks}{ext}");
    }
}
