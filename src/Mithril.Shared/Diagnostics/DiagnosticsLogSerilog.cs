using System.IO;
using Serilog;
using Serilog.Core;
using Serilog.Events;
using Serilog.Formatting.Compact;
using MelLogLevel = Microsoft.Extensions.Logging.LogLevel;

namespace Mithril.Shared.Diagnostics;

/// <summary>
/// Serilog file setup for Mithril diagnostics (Compact JSON, rolling policy).
/// </summary>
internal static class DiagnosticsLogSerilog
{
    public static Logger CreateFileLogger(string logDirectory, params ILogEventEnricher[] enrichers)
    {
        var config = new LoggerConfiguration()
            .MinimumLevel.Verbose();

        if (enrichers is { Length: > 0 })
            config = config.Enrich.With(enrichers);

        return config
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

    public static LogEventLevel Map(DiagnosticLevel level) => level switch
    {
        DiagnosticLevel.Trace => LogEventLevel.Verbose,
        DiagnosticLevel.Info => LogEventLevel.Information,
        DiagnosticLevel.Warn => LogEventLevel.Warning,
        DiagnosticLevel.Error => LogEventLevel.Error,
        _ => LogEventLevel.Information,
    };

    public static DiagnosticLevel Map(MelLogLevel level) => level switch
    {
        MelLogLevel.Trace => DiagnosticLevel.Trace,
        MelLogLevel.Debug => DiagnosticLevel.Trace,
        MelLogLevel.Information => DiagnosticLevel.Info,
        MelLogLevel.Warning => DiagnosticLevel.Warn,
        MelLogLevel.Error => DiagnosticLevel.Error,
        MelLogLevel.Critical => DiagnosticLevel.Error,
        _ => DiagnosticLevel.Info,
    };

    public static LogEventLevel MapMel(MelLogLevel level) => Map(Map(level));

    /// <summary>
    /// Migrates pre-rebrand log artifacts into the unified <c>logs\</c> directory:
    /// <list type="bullet">
    /// <item>renames pre-rebrand <c>gorgon-*.json</c> diagnostic files to <c>mithril-*-prebrand.json</c>;</item>
    /// <item>moves the old root-level <c>boot.log</c>/<c>crash.log</c> (which used to live in the
    /// parent <c>Shell\</c> directory) into <c>logs\</c> as <c>mithril-boot-prebrand.log</c> /
    /// <c>mithril-crash-prebrand.log</c>, never clobbering the live <c>mithril-boot.log</c>
    /// the current run may already have written.</item>
    /// </list>
    /// Idempotent and per-file fault tolerant.
    /// </summary>
    internal static void MigrateLegacyLogFiles(Action<DiagnosticLevel, string, string> write, string logDirectory)
    {
        MigrateLegacyRootLogFiles(write, logDirectory);

        IEnumerable<string> legacy;
        try
        {
            legacy = Directory.EnumerateFiles(logDirectory, "gorgon-*.json", SearchOption.TopDirectoryOnly);
        }
        catch (Exception ex)
        {
            write(DiagnosticLevel.Warn, "SerilogSink",
                $"Could not enumerate legacy log files in {logDirectory}: {ex.Message}");
            return;
        }

        foreach (var src in legacy)
        {
            try
            {
                var fileName = Path.GetFileName(src);
                var rest = fileName.Substring("gorgon-".Length);
                var stem = Path.GetFileNameWithoutExtension(rest);
                var ext = Path.GetExtension(rest);

                var target = ResolveNonClashingTarget(logDirectory, stem, ext);
                File.Move(src, target);
                write(DiagnosticLevel.Info, "SerilogSink",
                    $"Renamed legacy log file {fileName} -> {Path.GetFileName(target)}");
            }
            catch (Exception ex)
            {
                write(DiagnosticLevel.Warn, "SerilogSink",
                    $"Failed to migrate legacy log file {src}: {ex.Message}");
            }
        }
    }

    /// <summary>
    /// Moves the pre-unification root-level <c>boot.log</c> / <c>crash.log</c> (formerly written
    /// directly under <c>Shell\</c>) into the unified <c>logs\</c> directory under a non-clashing
    /// <c>mithril-{name}-prebrand.log</c> name. The old files used append-per-write (open/close),
    /// so they are not locked and can be moved.
    /// </summary>
    private static void MigrateLegacyRootLogFiles(Action<DiagnosticLevel, string, string> write, string logDirectory)
    {
        var parentDir = Path.GetDirectoryName(logDirectory);
        if (string.IsNullOrEmpty(parentDir))
            return;

        foreach (var legacyName in new[] { "boot.log", "crash.log" })
        {
            var src = Path.Combine(parentDir, legacyName);
            if (!File.Exists(src))
                continue;

            try
            {
                var stem = Path.GetFileNameWithoutExtension(legacyName);
                var ext = Path.GetExtension(legacyName);
                var target = ResolveNonClashingTarget(logDirectory, stem, ext);
                File.Move(src, target);
                write(DiagnosticLevel.Info, "SerilogSink",
                    $"Moved legacy log file {legacyName} -> {Path.GetFileName(target)}");
            }
            catch (Exception ex)
            {
                write(DiagnosticLevel.Warn, "SerilogSink",
                    $"Failed to migrate legacy root log file {src}: {ex.Message}");
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

        var ticks = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        return Path.Combine(logDirectory, $"mithril-{stem}-prebrand_{ticks}{ext}");
    }
}
