using System.IO;
using FluentAssertions;
using Mithril.Shared.Diagnostics;
using Xunit;

namespace Mithril.Shared.Tests.Diagnostics;

/// <summary>
/// Pre-rebrand Serilog rolling files were named <c>gorgon-*.json</c>; the prefix
/// changed to <c>mithril-</c> in the rebrand commit but on-disk files persisted.
/// These tests pin the migration that happens at sink construction.
/// </summary>
public class SerilogDiagnosticsSinkMigrationTests
{
    private sealed class CapturingSink : IDiagnosticsSink
    {
        public List<(DiagnosticLevel Level, string Category, string Message)> Entries { get; } = new();
        public void Write(DiagnosticLevel level, string category, string message) =>
            Entries.Add((level, category, message));
        public IReadOnlyList<DiagnosticEntry> Snapshot() => Array.Empty<DiagnosticEntry>();
        public event EventHandler<DiagnosticEntry>? EntryAdded { add { } remove { } }
    }

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
            var diag = new CapturingSink();

            SerilogDiagnosticsSink.MigrateLegacyLogFiles(diag, dir);

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
        // The real reason -prebrand exists: on at least one install, gorgon-20260425.json
        // and mithril-20260425.json both exist on disk for the same date.
        var dir = FreshDir();
        try
        {
            File.WriteAllText(Path.Combine(dir, "gorgon-20260425.json"), "old");
            File.WriteAllText(Path.Combine(dir, "mithril-20260425.json"), "new");
            var diag = new CapturingSink();

            SerilogDiagnosticsSink.MigrateLegacyLogFiles(diag, dir);

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
            var diag = new CapturingSink();

            SerilogDiagnosticsSink.MigrateLegacyLogFiles(diag, dir);

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
            var diag = new CapturingSink();

            SerilogDiagnosticsSink.MigrateLegacyLogFiles(diag, dir);
            SerilogDiagnosticsSink.MigrateLegacyLogFiles(diag, dir); // run twice

            File.Exists(Path.Combine(dir, "mithril-20260425.json")).Should().BeTrue();
            // No warnings emitted — only the optional info log on actual renames.
            diag.Entries.Should().NotContain(e => e.Level == DiagnosticLevel.Warn);
        }
        finally { Directory.Delete(dir, recursive: true); }
    }

    [Fact]
    public void Logs_Warning_If_Directory_Missing()
    {
        var dir = Path.Combine(Path.GetTempPath(), $"mithril-sink-missing-{Guid.NewGuid():N}");
        var diag = new CapturingSink();

        SerilogDiagnosticsSink.MigrateLegacyLogFiles(diag, dir);

        diag.Entries.Should().Contain(e =>
            e.Level == DiagnosticLevel.Warn && e.Category == "SerilogSink");
    }
}
