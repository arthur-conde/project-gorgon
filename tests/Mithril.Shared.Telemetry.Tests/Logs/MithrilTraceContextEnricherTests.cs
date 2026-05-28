using System.Diagnostics;
using System.Linq;
using FluentAssertions;
using Mithril.Shared.Telemetry.Logs;
using Serilog;
using Serilog.Sinks.InMemory;
using Xunit;

namespace Mithril.Shared.Telemetry.Tests.Logs;

public class MithrilTraceContextEnricherTests
{
    [Fact]
    public void Stamps_trace_id_and_span_id_when_activity_active()
    {
        var sink = new InMemorySink();
        var logger = new LoggerConfiguration()
            .Enrich.With(new MithrilTraceContextEnricher())
            .WriteTo.Sink(sink)
            .CreateLogger();

        using var src = new ActivitySource("Mithril.Test");
        using var listener = new ActivityListener
        {
            ShouldListenTo = source => source.Name == "Mithril.Test",
            Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllData
        };
        ActivitySource.AddActivityListener(listener);

        using (var act = src.StartActivity("scope"))
        {
            logger.Information("inside");
        }

        sink.LogEvents.Should().HaveCount(1);
        var ev = sink.LogEvents.Single();
        ev.Properties.Should().ContainKey("trace_id");
        ev.Properties.Should().ContainKey("span_id");
    }

    [Fact]
    public void Omits_properties_when_no_activity()
    {
        var sink = new InMemorySink();
        var logger = new LoggerConfiguration()
            .Enrich.With(new MithrilTraceContextEnricher())
            .WriteTo.Sink(sink)
            .CreateLogger();
        logger.Information("no activity");
        var ev = sink.LogEvents.Single();
        ev.Properties.Should().NotContainKey("trace_id");
        ev.Properties.Should().NotContainKey("span_id");
    }
}
