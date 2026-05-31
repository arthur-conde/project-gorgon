using System;
using System.IO;
using FluentAssertions;
using Mithril.Shell.ViewModels;
using Xunit;

namespace Mithril.Shell.Tests;

public sealed class LogDirectoryCleanerTests
{
    private static string FreshDir()
    {
        var dir = Path.Combine(Path.GetTempPath(), $"mithril-cleaner-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        return dir;
    }

    [Fact]
    public void Deletes_Only_Matching_Pattern()
    {
        var dir = FreshDir();
        try
        {
            File.WriteAllText(Path.Combine(dir, "mithril-20260101.json"), "a");
            File.WriteAllText(Path.Combine(dir, "mithril-20260102.json"), "b");
            File.WriteAllText(Path.Combine(dir, "mithril-boot.log"), "boot");
            File.WriteAllText(Path.Combine(dir, "mithril-crash.log"), "crash");

            var result = LogDirectoryCleaner.Clean(new[]
            {
                new LogDirectoryCleaner.CleanTarget(dir, "mithril-*.json"),
            });

            result.Removed.Should().Be(2);
            result.Skipped.Should().Be(0);
            Directory.GetFiles(dir, "*.json").Should().BeEmpty();
            // .log files untouched
            File.Exists(Path.Combine(dir, "mithril-boot.log")).Should().BeTrue();
            File.Exists(Path.Combine(dir, "mithril-crash.log")).Should().BeTrue();
        }
        finally { Directory.Delete(dir, recursive: true); }
    }

    [Fact]
    public void Deletes_All_Logs_And_Perf_Across_Multiple_Targets()
    {
        var logs = FreshDir();
        var perf = FreshDir();
        try
        {
            File.WriteAllText(Path.Combine(logs, "mithril-20260101.json"), "a");
            File.WriteAllText(Path.Combine(logs, "mithril-boot.log"), "boot");
            File.WriteAllText(Path.Combine(logs, "mithril-crash.log"), "crash");
            File.WriteAllText(Path.Combine(perf, "session-1.jsonl"), "p1");
            File.WriteAllText(Path.Combine(perf, "session-2.jsonl"), "p2");

            var result = LogDirectoryCleaner.Clean(new[]
            {
                new LogDirectoryCleaner.CleanTarget(logs, "*.json"),
                new LogDirectoryCleaner.CleanTarget(logs, "*.log"),
                new LogDirectoryCleaner.CleanTarget(perf, "*"),
            });

            result.Removed.Should().Be(5);
            result.Skipped.Should().Be(0);
            Directory.GetFiles(logs).Should().BeEmpty();
            Directory.GetFiles(perf).Should().BeEmpty();
        }
        finally
        {
            Directory.Delete(logs, recursive: true);
            Directory.Delete(perf, recursive: true);
        }
    }

    [Fact]
    public void Skips_Locked_File_Without_Throwing()
    {
        var dir = FreshDir();
        try
        {
            File.WriteAllText(Path.Combine(dir, "free.json"), "x");
            var lockedPath = Path.Combine(dir, "locked.json");
            File.WriteAllText(lockedPath, "y");

            // Hold the file open with no sharing, mirroring Serilog's shared:false handle.
            using (var held = new FileStream(lockedPath, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
            {
                var result = LogDirectoryCleaner.Clean(new[]
                {
                    new LogDirectoryCleaner.CleanTarget(dir, "*.json"),
                });

                result.Removed.Should().Be(1);
                result.Skipped.Should().Be(1);
                File.Exists(Path.Combine(dir, "free.json")).Should().BeFalse();
                File.Exists(lockedPath).Should().BeTrue();
            }
        }
        finally { Directory.Delete(dir, recursive: true); }
    }

    [Fact]
    public void Missing_Directory_Contributes_Nothing()
    {
        var missing = Path.Combine(Path.GetTempPath(), $"mithril-cleaner-missing-{Guid.NewGuid():N}");

        var result = LogDirectoryCleaner.Clean(new[]
        {
            new LogDirectoryCleaner.CleanTarget(missing, "*"),
        });

        result.Removed.Should().Be(0);
        result.Skipped.Should().Be(0);
    }
}
