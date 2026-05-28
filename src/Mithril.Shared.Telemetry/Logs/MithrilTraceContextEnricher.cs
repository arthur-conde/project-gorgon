using System.Diagnostics;
using Serilog.Core;
using Serilog.Events;

namespace Mithril.Shared.Telemetry.Logs;

/// <summary>
/// Stamps <c>trace_id</c> and <c>span_id</c> properties onto every Serilog
/// log event written inside an <see cref="Activity"/> scope. Lets the on-disk
/// diagnostics .json log correlate with span timing data captured by any
/// listener (perf-recorder file exporter today, OTLP exporter once enabled).
///
/// No-op outside an active Activity (the common case for shell-startup
/// logging before any source is wrapped).
/// </summary>
/// <remarks>
/// The property keys <c>trace_id</c> and <c>span_id</c> are lowercase-underscored
/// per the OpenTelemetry logs semantic convention, deliberately differing from
/// Mithril's PascalCase MEL property convention so downstream OTel collectors
/// recognise them without remapping.
/// </remarks>
public sealed class MithrilTraceContextEnricher : ILogEventEnricher
{
    public void Enrich(LogEvent logEvent, ILogEventPropertyFactory propertyFactory)
    {
        var act = Activity.Current;
        if (act is null) return;
        logEvent.AddPropertyIfAbsent(propertyFactory.CreateProperty("trace_id", act.TraceId.ToString()));
        logEvent.AddPropertyIfAbsent(propertyFactory.CreateProperty("span_id", act.SpanId.ToString()));
    }
}
