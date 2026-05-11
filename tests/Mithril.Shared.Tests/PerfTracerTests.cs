using System.IO;
using FluentAssertions;
using Mithril.Shared.Diagnostics.Performance;
using Xunit;

namespace Mithril.Shared.Tests;

public class PerfTracerTests
{
    private static string FreshTempDir()
    {
        var path = Path.Combine(Path.GetTempPath(), "mithril-perftrace-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        return path;
    }

    private static SessionHeader SampleHeader() =>
        new(Build: "test", Os: "test-os", Gpu: "", RefreshRateHz: 0, Dpi: 96.0,
            ActiveCharacter: null, ActiveServer: null, LoadedModules: ["samwise", "pippin"]);

    [Fact]
    public void Inactive_state_is_a_noop()
    {
        var dir = FreshTempDir();
        try
        {
            using var tracer = new PerfTracer(dir);
            tracer.IsActive.Should().BeFalse();
            tracer.CurrentSessionPath.Should().BeNull();

            // None of these may throw, none of these may write a file.
            using (var s = tracer.Scope("does-not-matter", new { x = 1 })) { }
            tracer.EmitFrameSummary(10, 16.0, 16.0, 17.0, 18.0, 0);
            tracer.EmitDispatcher("Background", 1.0, 2.0, 3);
            tracer.EmitGc(2, 5.0);
            tracer.EmitBindingError("System.Windows.Data Error: 40");
            tracer.EmitInputLatency("mouse", 12.3);
            tracer.EmitScope("manual", 7.5, null);
            tracer.EmitModuleActivated("samwise", 42.0);
            tracer.EmitRefFetch("items", false, 500.0, 1024);

            Directory.EnumerateFiles(dir, "*.jsonl").Should().BeEmpty(
                "no session is active so the tracer must not have opened a file");
        }
        finally { TryCleanup(dir); }
    }

    [Fact]
    public void Start_session_writes_a_session_header_then_subsequent_events()
    {
        var dir = FreshTempDir();
        try
        {
            using (var tracer = new PerfTracer(dir))
            {
                tracer.StartSession(SampleHeader());
                tracer.IsActive.Should().BeTrue();
                tracer.CurrentSessionPath.Should().NotBeNull();

                tracer.EmitModuleActivated("samwise", 12.3);
                tracer.EmitRefFetch("items", false, 250.0, 2048);
                tracer.StopSession();
            }

            var files = Directory.GetFiles(dir, "perf-*.jsonl");
            files.Should().HaveCount(1);
            var lines = File.ReadAllLines(files[0]).Where(l => !string.IsNullOrWhiteSpace(l)).ToArray();
            lines.Should().HaveCountGreaterOrEqualTo(3);
            lines[0].Should().Contain("\"Kind\":\"session_header\"");
            lines[0].Should().Contain("\"Build\":\"test\"");
            string.Join('\n', lines).Should().Contain("\"Kind\":\"module_activated\"");
            string.Join('\n', lines).Should().Contain("\"Kind\":\"ref_fetch\"");
        }
        finally { TryCleanup(dir); }
    }

    [Fact]
    public void Scope_reports_a_nonzero_duration()
    {
        var dir = FreshTempDir();
        try
        {
            using (var tracer = new PerfTracer(dir))
            {
                tracer.StartSession(SampleHeader());
                using (var s = tracer.Scope("test.work", new { iterations = 3 }))
                {
                    // Sleep is the cheapest way to guarantee Stopwatch elapsed > 0.
                    Thread.Sleep(5);
                }
                tracer.StopSession();
            }

            var line = File.ReadAllLines(Directory.GetFiles(dir, "perf-*.jsonl").Single())
                .Single(l => l.Contains("\"Kind\":\"scope\""));
            line.Should().Contain("\"Name\":\"test.work\"");
            // Just make sure DurationMs is present and parses as a positive number — value depends on host.
            var idx = line.IndexOf("\"DurationMs\":", StringComparison.Ordinal);
            idx.Should().BeGreaterThan(-1);
            var tail = line[(idx + "\"DurationMs\":".Length)..];
            var end = tail.IndexOfAny([',', '}']);
            double.Parse(tail[..end], System.Globalization.CultureInfo.InvariantCulture)
                .Should().BeGreaterThan(0);
        }
        finally { TryCleanup(dir); }
    }

    [Fact]
    public void IsActiveChanged_fires_on_Start_and_Stop()
    {
        var dir = FreshTempDir();
        try
        {
            using var tracer = new PerfTracer(dir);
            var transitions = new List<bool>();
            tracer.IsActiveChanged += (_, _) => transitions.Add(tracer.IsActive);

            tracer.StartSession(SampleHeader());
            tracer.StopSession();

            transitions.Should().HaveCount(2, "consumers depend on observing both start and stop transitions");
            transitions[0].Should().BeTrue();
            transitions[1].Should().BeFalse();
        }
        finally { TryCleanup(dir); }
    }

    [Fact]
    public void PruneOldSessions_keeps_only_N_newest()
    {
        var dir = FreshTempDir();
        try
        {
            // Create six dummy session files with descending creation times.
            var now = DateTime.UtcNow;
            for (var i = 0; i < 6; i++)
            {
                var path = Path.Combine(dir, $"perf-2026010{i}-000000.jsonl");
                File.WriteAllText(path, "x");
                File.SetCreationTimeUtc(path, now.AddMinutes(-i));
            }

            PerfTracer.PruneOldSessions(dir, retain: 3);

            Directory.GetFiles(dir, "perf-*.jsonl").Should().HaveCount(3,
                "the four oldest files should have been deleted");
        }
        finally { TryCleanup(dir); }
    }

    private static void TryCleanup(string dir)
    {
        try { Directory.Delete(dir, recursive: true); } catch { /* test cleanup is best-effort */ }
    }
}
