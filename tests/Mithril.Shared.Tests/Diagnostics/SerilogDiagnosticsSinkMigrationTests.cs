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
}
