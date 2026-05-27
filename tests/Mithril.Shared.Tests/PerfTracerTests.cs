using System.IO;
using FluentAssertions;
using Mithril.Shared.Diagnostics.Performance;
using Mithril.Shared.Diagnostics.Telemetry;
using Xunit;

namespace Mithril.Shared.Tests;

/// <summary>
/// Schema-parity tests for the perf-recorder pipeline. Producers emit via
/// <see cref="MithrilActivitySources"/> + <see cref="MithrilMeters"/>; the
/// recorder's internal listener writes the JSON-lines schema documented in
/// <c>docs/perf-trace-schema.md</c>. These tests fix the on-disk shape so
/// the <c>mithril-logs</c> MCP server and saved jq recipes keep working
/// across the producer-API change.
/// </summary>
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
            ActiveCharacter: null, ActiveServer: null, LoadedModules: ["samwise", "pippin"],
            RenderTier: 2, RenderMode: "Default", IsRemoteSession: false);

    [Fact]
    public void Inactive_state_is_a_noop()
    {
        var dir = FreshTempDir();
        try
        {
            using var recorder = new PerfRecorder(dir);
            recorder.IsActive.Should().BeFalse();
            recorder.CurrentSessionPath.Should().BeNull();

            // With no session attached, ActivitySource.StartActivity returns null
            // and Meter.Record is a no-op. No file should appear.
            using (var act = MithrilActivitySources.ShellModules.StartActivity("activate"))
                act?.SetTag("module.id", "samwise");
            MithrilMeters.Wpf.FrameIntervalMs.Record(16.0);

            Directory.EnumerateFiles(dir, "*.jsonl").Should().BeEmpty(
                "no session is active so the recorder must not have opened a file");
        }
        finally { TryCleanup(dir); }
    }

    [Fact]
    public void Start_session_writes_a_session_header_then_subsequent_events()
    {
        var dir = FreshTempDir();
        try
        {
            using (var recorder = new PerfRecorder(dir))
            {
                recorder.Start(SampleHeader());
                recorder.IsActive.Should().BeTrue();
                recorder.CurrentSessionPath.Should().NotBeNull();

                using (var act = MithrilActivitySources.ShellModules.StartActivity("activate"))
                {
                    act?.SetTag("module.id", "samwise");
                    Thread.Sleep(2);
                }
                using (var act = MithrilActivitySources.Reference.StartActivity("fetch"))
                {
                    act?.SetTag("file", "items");
                    act?.SetTag("cache_hit", false);
                    act?.SetTag("bytes", 2048L);
                }
                recorder.Stop();
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
    public void IsActiveChanged_fires_on_Start_and_Stop()
    {
        var dir = FreshTempDir();
        try
        {
            using var recorder = new PerfRecorder(dir);
            var transitions = new List<bool>();
            recorder.IsActiveChanged += (_, _) => transitions.Add(recorder.IsActive);

            recorder.Start(SampleHeader());
            recorder.Stop();

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

            PerfRecorder.PruneOldSessions(dir, retain: 3);

            Directory.GetFiles(dir, "perf-*.jsonl").Should().HaveCount(3,
                "the four oldest files should have been deleted");
        }
        finally { TryCleanup(dir); }
    }

    [Fact]
    public void Module_activate_activity_serialises_to_module_activated_record()
    {
        var dir = FreshTempDir();
        try
        {
            using (var recorder = new PerfRecorder(dir))
            {
                recorder.Start(SampleHeader());
                using (var act = MithrilActivitySources.ShellModules.StartActivity("activate"))
                {
                    act?.SetTag("module.id", "samwise");
                    Thread.Sleep(3);
                }
                recorder.Stop();
            }

            var line = File.ReadAllLines(Directory.GetFiles(dir, "perf-*.jsonl").Single())
                .Single(l => l.Contains("\"Kind\":\"module_activated\""));
            line.Should().Contain("\"ModuleId\":\"samwise\"");
            line.Should().Contain("\"DurationMs\":");
        }
        finally { TryCleanup(dir); }
    }

    [Fact]
    public void Ref_fetch_activity_serialises_with_cache_hit_and_bytes()
    {
        var dir = FreshTempDir();
        try
        {
            using (var recorder = new PerfRecorder(dir))
            {
                recorder.Start(SampleHeader());
                using (var act = MithrilActivitySources.Reference.StartActivity("fetch"))
                {
                    act?.SetTag("file", "recipes");
                    act?.SetTag("cache_hit", true);
                    act?.SetTag("bytes", 4096L);
                }
                recorder.Stop();
            }

            var line = File.ReadAllLines(Directory.GetFiles(dir, "perf-*.jsonl").Single())
                .Single(l => l.Contains("\"Kind\":\"ref_fetch\""));
            line.Should().Contain("\"File\":\"recipes\"");
            line.Should().Contain("\"CacheHit\":true");
            line.Should().Contain("\"Bytes\":4096");
        }
        finally { TryCleanup(dir); }
    }

    private static void TryCleanup(string dir)
    {
        try { Directory.Delete(dir, recursive: true); } catch { /* test cleanup is best-effort */ }
    }
}
