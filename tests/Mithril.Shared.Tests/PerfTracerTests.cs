using System.IO;
using System.Text.Json;
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
[Collection(TelemetryTestCollection.Name)]
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

    private static JsonElement FindRecord(string dir, string kind) =>
        File.ReadAllLines(Directory.GetFiles(dir, "perf-*.jsonl").Single())
            .Where(l => !string.IsNullOrWhiteSpace(l))
            .Select(l => JsonDocument.Parse(l).RootElement)
            .Single(e => e.GetProperty("Kind").GetString() == kind);

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

            // Typed JSON parse — substring matching would miss field reorder, numeric
            // formatting drift (e.g. DurationMs as "2.0" vs "2"), null-vs-absent
            // semantics, and string-escape differences. Downstream consumers
            // (mithril-logs MCP, jq) decode JSON, so the parity contract is the parsed
            // graph, not the raw bytes.
            var record = FindRecord(dir, "module_activated");
            record.GetProperty("ModuleId").GetString().Should().Be("samwise");
            record.GetProperty("DurationMs").GetDouble().Should().BeGreaterThan(0);
        }
        finally { TryCleanup(dir); }
    }

    [Fact]
    public void Ref_fetch_activity_serialises_with_cache_hit_bytes_and_outcome()
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
                    act?.SetTag("outcome", "cache");
                    act?.SetTag("bytes", 4096L);
                }
                recorder.Stop();
            }

            var record = FindRecord(dir, "ref_fetch");
            record.GetProperty("File").GetString().Should().Be("recipes");
            record.GetProperty("CacheHit").GetBoolean().Should().BeTrue();
            record.GetProperty("Outcome").GetString().Should().Be("cache");
            record.GetProperty("Bytes").GetInt64().Should().Be(4096);
            // Schema-parity (legacy): retain the original substring check too so we
            // notice if the property-name case ever drifts.
            File.ReadAllLines(Directory.GetFiles(dir, "perf-*.jsonl").Single())
                .Single(l => l.Contains("\"Kind\":\"ref_fetch\""))
                .Should().Contain("\"File\":\"recipes\"");
        }
        finally { TryCleanup(dir); }
    }

    [Fact]
    public void Arda_compose_activity_serialises_to_arda_compose_record_end_to_end()
    {
        // Important #10: producer-side ArdaInstrumentationTests asserts the activity
        // shape, but the JSON-lines record shape — which the exporter dispatch arm
        // generates — was untested. A rename of `compose.X` → `composer.X` in the
        // producer would silently break the consumer with green producer tests.
        var dir = FreshTempDir();
        try
        {
            using (var recorder = new PerfRecorder(dir))
            {
                recorder.Start(SampleHeader());
                using (var act = Arda.Abstractions.Diagnostics.ArdaActivitySources.Composition.StartActivity("compose.InventoryComposer"))
                {
                    act?.SetTag("event", "InventoryItemAdded");
                }
                recorder.Stop();
            }

            var record = FindRecord(dir, "arda_compose");
            record.GetProperty("Composer").GetString().Should().Be("InventoryComposer");
            record.GetProperty("EventType").GetString().Should().Be("InventoryItemAdded");
            record.GetProperty("DurationMs").GetDouble().Should().BeGreaterOrEqualTo(0);
        }
        finally { TryCleanup(dir); }
    }

    [Fact]
    public void Concurrent_counter_increments_aggregate_to_a_single_meter_counter_record()
    {
        // Critical #3: counter aggregation (AccumulateCounter → FlushCounters → JSON)
        // had no end-to-end test. This stresses the Interlocked.Add/Exchange pair
        // under concurrent producers and asserts the aggregated sum lands as a single
        // `meter_counter` line with the expected tag set.
        var dir = FreshTempDir();
        try
        {
            using (var recorder = new PerfRecorder(dir))
            {
                recorder.Start(SampleHeader());

                const int incrementsPerThread = 1_000;
                const int threadCount = 10;
                Parallel.For(0, threadCount, _ =>
                {
                    for (var i = 0; i < incrementsPerThread; i++)
                    {
                        Arda.Abstractions.Diagnostics.ArdaMeters.LinesParsed.Add(1,
                            new KeyValuePair<string, object?>("source", "player"));
                    }
                });

                // FlushCounters runs on a 1s timer inside the exporter. Stop() also
                // flushes synchronously via Dispose, so a deterministic stop is enough.
                recorder.Stop();
            }

            // Aggregation must collapse 10_000 measurements into one record per
            // (instrument, tag-set), with the sum exactly equal to the total.
            var counterRecords = File.ReadAllLines(Directory.GetFiles(dir, "perf-*.jsonl").Single())
                .Where(l => !string.IsNullOrWhiteSpace(l))
                .Select(l => JsonDocument.Parse(l).RootElement)
                .Where(e => e.GetProperty("Kind").GetString() == "meter_counter"
                            && e.GetProperty("Instrument").GetString() == "mithril.arda.lines_parsed")
                .ToList();
            counterRecords.Should().NotBeEmpty("at least the final Dispose-flush must emit");
            counterRecords.Sum(e => e.GetProperty("Sum").GetInt64())
                .Should().Be(10_000, "all increments must aggregate to the JSON sum without loss");
        }
        finally { TryCleanup(dir); }
    }

    private static void TryCleanup(string dir)
    {
        try { Directory.Delete(dir, recursive: true); } catch { /* test cleanup is best-effort */ }
    }
}
