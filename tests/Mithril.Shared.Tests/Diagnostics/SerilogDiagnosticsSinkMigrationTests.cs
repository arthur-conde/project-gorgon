using System.IO;
using FluentAssertions;
using Mithril.Shared.Diagnostics;
using Xunit;

namespace Mithril.Shared.Tests.Diagnostics;

/// <summary>
/// Pre-rebrand Serilog rolling files were named <c>gorgon-*.json</c>; the prefix
/// changed to <c>mithril-</c> in the rebrand commit but on-disk files persisted.
/// These tests pin the migration that happens at provider construction.
/// </summary>
public class DiagnosticsLogSerilogMigrationTests
{
    private static string FreshDir()
    {
        var dir = Path.Combine(Path.GetTempPath(), $"mithril-sink-migration-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        return dir;
    }

    [Fact]
    public void Renames_Legacy_Files_To_Mithril_Prefix()
    {
        var dir = FreshDir();
        try
        {
            File.WriteAllText(Path.Combine(dir, "gorgon-20260415.json"), "x");
            File.WriteAllText(Path.Combine(dir, "gorgon-20260416_001.json"), "y");
            var entries = new List<(DiagnosticLevel Level, string Category, string Message)>();
            DiagnosticsLogSerilog.MigrateLegacyLogFiles(
                (level, category, message) => entries.Add((level, category, message)),
                dir);

            File.Exists(Path.Combine(dir, "gorgon-20260415.json")).Should().BeFalse();
            File.Exists(Path.Combine(dir, "gorgon-20260416_001.json")).Should().BeFalse();
            File.Exists(Path.Combine(dir, "mithril-20260415-prebrand.json")).Should().BeTrue();
            File.Exists(Path.Combine(dir, "mithril-20260416_001-prebrand.json")).Should().BeTrue();
        }
        finally { Directory.Delete(dir, recursive: true); }
    }

    [Fact]
    public void Does_Not_Clash_With_Existing_Mithril_File_For_Same_Date()
    {
        var dir = FreshDir();
        try
        {
            File.WriteAllText(Path.Combine(dir, "gorgon-20260425.json"), "old");
            File.WriteAllText(Path.Combine(dir, "mithril-20260425.json"), "new");
            DiagnosticsLogSerilog.MigrateLegacyLogFiles((_, _, _) => { }, dir);

            File.Exists(Path.Combine(dir, "gorgon-20260425.json")).Should().BeFalse();
            File.Exists(Path.Combine(dir, "mithril-20260425.json")).Should().BeTrue();
            File.Exists(Path.Combine(dir, "mithril-20260425-prebrand.json")).Should().BeTrue();
            File.ReadAllText(Path.Combine(dir, "mithril-20260425.json")).Should().Be("new");
            File.ReadAllText(Path.Combine(dir, "mithril-20260425-prebrand.json")).Should().Be("old");
        }
        finally { Directory.Delete(dir, recursive: true); }
    }

    [Fact]
    public void Disambiguates_When_Prebrand_Target_Also_Exists()
    {
        var dir = FreshDir();
        try
        {
            File.WriteAllText(Path.Combine(dir, "gorgon-20260425.json"), "old");
            File.WriteAllText(Path.Combine(dir, "mithril-20260425-prebrand.json"), "previous-attempt");
            DiagnosticsLogSerilog.MigrateLegacyLogFiles((_, _, _) => { }, dir);

            File.Exists(Path.Combine(dir, "gorgon-20260425.json")).Should().BeFalse();
            File.Exists(Path.Combine(dir, "mithril-20260425-prebrand.json")).Should().BeTrue();
            File.Exists(Path.Combine(dir, "mithril-20260425-prebrand_001.json")).Should().BeTrue();
            File.ReadAllText(Path.Combine(dir, "mithril-20260425-prebrand.json")).Should().Be("previous-attempt");
            File.ReadAllText(Path.Combine(dir, "mithril-20260425-prebrand_001.json")).Should().Be("old");
        }
        finally { Directory.Delete(dir, recursive: true); }
    }

    [Fact]
    public void Is_Idempotent_When_No_Legacy_Files_Present()
    {
        var dir = FreshDir();
        try
        {
            File.WriteAllText(Path.Combine(dir, "mithril-20260425.json"), "new");
            var entries = new List<(DiagnosticLevel Level, string Category, string Message)>();
            void Capture(DiagnosticLevel level, string category, string message) =>
                entries.Add((level, category, message));

            DiagnosticsLogSerilog.MigrateLegacyLogFiles(Capture, dir);
            DiagnosticsLogSerilog.MigrateLegacyLogFiles(Capture, dir);

            File.Exists(Path.Combine(dir, "mithril-20260425.json")).Should().BeTrue();
            entries.Should().NotContain(e => e.Level == DiagnosticLevel.Warn);
        }
        finally { Directory.Delete(dir, recursive: true); }
    }

    [Fact]
    public void Logs_Warning_If_Directory_Missing()
    {
        var dir = Path.Combine(Path.GetTempPath(), $"mithril-sink-missing-{Guid.NewGuid():N}");
        var entries = new List<(DiagnosticLevel Level, string Category, string Message)>();

        DiagnosticsLogSerilog.MigrateLegacyLogFiles(
            (level, category, message) => entries.Add((level, category, message)),
            dir);

        entries.Should().Contain(e =>
            e.Level == DiagnosticLevel.Warn && e.Category == "SerilogSink");
    }

    /// <summary>
    /// Creates a <c>…\Shell\logs</c> directory inside a fresh temp root and returns
    /// the logs path. The parent (<c>…\Shell</c>) is where legacy root-level
    /// <c>boot.log</c>/<c>crash.log</c> historically lived.
    /// </summary>
    private static (string Parent, string LogsDir) FreshShellWithLogs()
    {
        var parent = Path.Combine(Path.GetTempPath(), $"mithril-shell-{Guid.NewGuid():N}");
        var logsDir = Path.Combine(parent, "logs");
        Directory.CreateDirectory(logsDir);
        return (parent, logsDir);
    }

    [Fact]
    public void Moves_Legacy_Root_BootLog_And_CrashLog_Into_Logs_Dir()
    {
        var (parent, logsDir) = FreshShellWithLogs();
        try
        {
            File.WriteAllText(Path.Combine(parent, "boot.log"), "boot");
            File.WriteAllText(Path.Combine(parent, "crash.log"), "crash");

            DiagnosticsLogSerilog.MigrateLegacyLogFiles((_, _, _) => { }, logsDir);

            File.Exists(Path.Combine(parent, "boot.log")).Should().BeFalse();
            File.Exists(Path.Combine(parent, "crash.log")).Should().BeFalse();

            File.Exists(Path.Combine(logsDir, "mithril-boot-prebrand.log")).Should().BeTrue();
            File.Exists(Path.Combine(logsDir, "mithril-crash-prebrand.log")).Should().BeTrue();
            File.ReadAllText(Path.Combine(logsDir, "mithril-boot-prebrand.log")).Should().Be("boot");
            File.ReadAllText(Path.Combine(logsDir, "mithril-crash-prebrand.log")).Should().Be("crash");
        }
        finally { Directory.Delete(parent, recursive: true); }
    }

    [Fact]
    public void Does_Not_Clobber_Live_Mithril_BootLog()
    {
        var (parent, logsDir) = FreshShellWithLogs();
        try
        {
            // The current run has already written the live boot log.
            File.WriteAllText(Path.Combine(logsDir, "mithril-boot.log"), "live");
            File.WriteAllText(Path.Combine(parent, "boot.log"), "old");

            DiagnosticsLogSerilog.MigrateLegacyLogFiles((_, _, _) => { }, logsDir);

            File.Exists(Path.Combine(parent, "boot.log")).Should().BeFalse();
            File.ReadAllText(Path.Combine(logsDir, "mithril-boot.log")).Should().Be("live");
            File.Exists(Path.Combine(logsDir, "mithril-boot-prebrand.log")).Should().BeTrue();
            File.ReadAllText(Path.Combine(logsDir, "mithril-boot-prebrand.log")).Should().Be("old");
        }
        finally { Directory.Delete(parent, recursive: true); }
    }

    [Fact]
    public void Disambiguates_When_Prebrand_BootLog_Target_Already_Exists()
    {
        var (parent, logsDir) = FreshShellWithLogs();
        try
        {
            File.WriteAllText(Path.Combine(logsDir, "mithril-boot-prebrand.log"), "previous-attempt");
            File.WriteAllText(Path.Combine(parent, "boot.log"), "old");

            DiagnosticsLogSerilog.MigrateLegacyLogFiles((_, _, _) => { }, logsDir);

            File.Exists(Path.Combine(parent, "boot.log")).Should().BeFalse();
            File.ReadAllText(Path.Combine(logsDir, "mithril-boot-prebrand.log")).Should().Be("previous-attempt");
            File.Exists(Path.Combine(logsDir, "mithril-boot-prebrand_001.log")).Should().BeTrue();
            File.ReadAllText(Path.Combine(logsDir, "mithril-boot-prebrand_001.log")).Should().Be("old");
        }
        finally { Directory.Delete(parent, recursive: true); }
    }

    [Fact]
    public void Is_Idempotent_When_No_Legacy_Root_Files_Present()
    {
        var (parent, logsDir) = FreshShellWithLogs();
        try
        {
            var entries = new List<(DiagnosticLevel Level, string Category, string Message)>();
            void Capture(DiagnosticLevel level, string category, string message) =>
                entries.Add((level, category, message));

            DiagnosticsLogSerilog.MigrateLegacyLogFiles(Capture, logsDir);
            DiagnosticsLogSerilog.MigrateLegacyLogFiles(Capture, logsDir);

            File.Exists(Path.Combine(parent, "boot.log")).Should().BeFalse();
            File.Exists(Path.Combine(parent, "crash.log")).Should().BeFalse();
            entries.Should().NotContain(e => e.Level == DiagnosticLevel.Warn);
        }
        finally { Directory.Delete(parent, recursive: true); }
    }

    /// <summary>
    /// Regression: the migration runs inside the <see cref="DiagnosticsLoggerProvider"/> ctor and
    /// reports each move via <c>Publish</c>, which dereferences the Serilog file logger. If the
    /// file logger is not yet constructed when the migration emits its first line, the ctor throws
    /// and aborts host build. This exercises the real ctor with a legacy root <c>boot.log</c>
    /// present (which forces a Publish during migration) and asserts it neither throws nor leaves
    /// the legacy file behind.
    /// </summary>
    [Fact]
    public void Provider_Ctor_Migrates_Root_BootLog_Without_Throwing()
    {
        var (parent, logsDir) = FreshShellWithLogs();
        try
        {
            File.WriteAllText(Path.Combine(parent, "boot.log"), "old-boot");

            var act = () =>
            {
                using var provider = new DiagnosticsLoggerProvider(logsDir);
            };

            act.Should().NotThrow();
            File.Exists(Path.Combine(parent, "boot.log")).Should().BeFalse();
            File.Exists(Path.Combine(logsDir, "mithril-boot-prebrand.log")).Should().BeTrue();
            File.ReadAllText(Path.Combine(logsDir, "mithril-boot-prebrand.log")).Should().Be("old-boot");
        }
        finally { Directory.Delete(parent, recursive: true); }
    }
}
