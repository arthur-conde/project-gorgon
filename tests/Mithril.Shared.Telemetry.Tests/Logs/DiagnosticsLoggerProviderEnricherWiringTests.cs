using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.Json;
using FluentAssertions;
using Microsoft.Extensions.Logging;
using Mithril.Shared.Diagnostics;
using Mithril.Shared.Telemetry.Logs;
using Xunit;

namespace Mithril.Shared.Telemetry.Tests.Logs;

/// <summary>
/// Regression for the Shell composition wiring: when <see cref="DiagnosticsLoggerProvider"/>
/// is constructed with the <see cref="MithrilTraceContextEnricher"/> (as the shell does in
/// <c>ShellComposition.AddMithrilShell</c>), on-disk diagnostics JSON entries emitted inside
/// an Activity scope must carry <c>trace_id</c> and <c>span_id</c> properties, while entries
/// emitted outside any Activity must not. Lives in the telemetry test assembly because that's
/// where the enricher type sits (Mithril.Shared cannot reference it without a dep cycle).
/// </summary>
public sealed class DiagnosticsLoggerProviderEnricherWiringTests
{
    [Fact]
    public void File_log_carries_trace_and_span_ids_when_emitted_inside_activity()
    {
        var dir = Path.Combine(Path.GetTempPath(), $"mithril-trace-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        try
        {
            using var src = new ActivitySource("Mithril.Test.EnricherWiring");
            using var listener = new ActivityListener
            {
                ShouldListenTo = source => source.Name == "Mithril.Test.EnricherWiring",
                Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllData,
            };
            ActivitySource.AddActivityListener(listener);

            using (var provider = new DiagnosticsLoggerProvider(
                       dir,
                       DiagnosticsLoggerProvider.DefaultCapacity,
                       new MithrilTraceContextEnricher()))
            {
                var logger = provider.CreateLogger("EnricherWiringTest");

                logger.LogInformation("before activity");
                using (var act = src.StartActivity("scope"))
                {
                    logger.LogInformation("inside activity");
                }
                logger.LogInformation("after activity");
            }

            var file = Directory.GetFiles(dir, "mithril-*.json").Single();
            var lines = File.ReadAllLines(file)
                .Where(l => !string.IsNullOrWhiteSpace(l))
                .Select(l => JsonDocument.Parse(l).RootElement)
                .ToList();

            lines.Should().HaveCountGreaterThanOrEqualTo(3);

            var before = lines.Single(e => e.GetProperty("Message").GetString() == "before activity");
            var inside = lines.Single(e => e.GetProperty("Message").GetString() == "inside activity");
            var after = lines.Single(e => e.GetProperty("Message").GetString() == "after activity");

            inside.TryGetProperty("trace_id", out var traceId).Should().BeTrue(
                "the inside-activity log entry should carry the enriched trace_id");
            inside.TryGetProperty("span_id", out var spanId).Should().BeTrue(
                "the inside-activity log entry should carry the enriched span_id");
            traceId.GetString().Should().NotBeNullOrWhiteSpace();
            spanId.GetString().Should().NotBeNullOrWhiteSpace();

            before.TryGetProperty("trace_id", out _).Should().BeFalse(
                "no Activity active: the enricher must remain a no-op");
            after.TryGetProperty("trace_id", out _).Should().BeFalse(
                "Activity disposed: the enricher must remain a no-op");
        }
        finally
        {
            try { Directory.Delete(dir, recursive: true); } catch { /* leftover files are harmless */ }
        }
    }
}
