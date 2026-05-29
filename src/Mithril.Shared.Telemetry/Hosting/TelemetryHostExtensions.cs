using System;
using System.Reflection;
using System.Text;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Mithril.Shared.Character;
using Mithril.Shared.Telemetry.Catalog;
using Mithril.Shared.Telemetry.Export;
using Mithril.Shared.Telemetry.Processing;
using Mithril.Shared.Telemetry.Settings;
using OpenTelemetry;
using OpenTelemetry.Exporter;
using OpenTelemetry.Logs;
using OpenTelemetry.Metrics;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;

namespace Mithril.Shared.Telemetry.Hosting;

/// <summary>
/// Shell composition entry point for opt-in OTLP export (mithril#815).
///
/// When <see cref="TelemetrySettings.EnableOtlpExport"/> is <c>false</c>, this
/// extension registers <strong>nothing</strong> — no <c>OpenTelemetryBuilder</c>,
/// no <c>ActivityListener</c> for <c>Mithril.*</c>, so producer-side
/// <c>StartActivity</c> returns <c>null</c> and the dispatch hot path stays
/// allocation-free (zero-overhead-when-off contract).
///
/// When enabled, registers:
/// <list type="bullet">
/// <item>Scrubber graph: <see cref="TagCatalog"/>,
///   <see cref="NewlySeenTagsObserver"/>, <see cref="ValueRedactor"/>,
///   <see cref="ExporterHealthMonitor"/>, and the per-pipeline scrubber
///   processors (<see cref="AllowlistAndRedactionProcessor"/> /
///   <see cref="ValueRedactionOnlyProcessor"/> for spans;
///   <see cref="LogScrubbingProcessor"/> /
///   <see cref="LogValueRedactionOnlyProcessor"/> for logs;
///   <see cref="MetricTagAllowlistView"/> for metric dimensions).</item>
/// <item>OpenTelemetry tracing for every <c>Mithril.*</c>
///   <see cref="System.Diagnostics.ActivitySource"/> with HTTP-client
///   auto-instrumentation and OTLP export.</item>
/// <item>OpenTelemetry metrics for every <c>Mithril.*</c>
///   <see cref="System.Diagnostics.Metrics.Meter"/> with OTLP export.</item>
/// <item>OpenTelemetry logs bridge with OTLP export, sharing the resource
///   attribute set with traces and metrics.</item>
/// </list>
///
/// <para><strong>Three-surface scrubber symmetry (mithril#841).</strong>
/// The same three-layer model — catalog membership → user
/// <see cref="TelemetrySettings.TagExports"/> override → value redaction —
/// runs on spans, metric dimensions, and log records. Spans + logs both use
/// a <c>BaseProcessor&lt;T&gt;</c> on the export pipeline and re-read
/// <c>TagExports</c> per record so user toggles are live. Metrics use the
/// OTel view API (<see cref="MetricTagAllowlistView"/>), which captures the
/// allowed tag-key set once per instrument observation — metric-only tags
/// are restart-required for v1. The
/// <see cref="TelemetrySettings.TrustEndpoint"/> bypass also applies
/// symmetrically: when on, the allowlist gate is skipped on all three
/// surfaces but <see cref="ValueRedactor"/> still scrubs paths + character
/// name from string values and the formatted log body.</para>
///
/// <para><strong>Hot-reload — partial.</strong>
/// The <see cref="IOptionsMonitor{T}"/> registered here is a real
/// <see cref="NotifyPropertyChangedOptionsMonitor{T}"/> that fires
/// <c>OnChange</c> on every settings-singleton mutation (the settings UI's
/// in-place edit + <c>Touch()</c> path). <see cref="TelemetrySettings.TagExports"/>
/// mutations flow through to the scrubber live (per-record read of
/// <c>IOptionsMonitor.CurrentValue.TagExports</c>) — the settings UI can toggle
/// a tag and the next exported span honours it.
/// Endpoint, headers, protocol, and service-name changes still require a
/// process restart: OTel SDK 1.15.x captures them in the exporter instance at
/// provider-build time (the <c>AddOtlpExporter(Action&lt;OtlpExporterOptions&gt;)</c>
/// callback runs once and <c>OtlpTraceExporter</c> snapshots them into its
/// transmission handler in its constructor — it never re-reads). A per-export
/// <c>IServiceProvider</c> factory overload that would permit live re-reads is
/// an open upstream request (open-telemetry/opentelemetry-dotnet#6537); until it
/// lands, live swapping these fields would require a full
/// <c>TracerProvider</c>/<c>MeterProvider</c>/<c>LoggerProvider</c> rebuild.
/// Tracked as a follow-up. mithril#833.</para>
///
/// <para><strong>Exporter health.</strong> An
/// <see cref="OtlpExporterEventListener"/> singleton is constructed eagerly so
/// the OTel SDK's internal exporter <see cref="System.Diagnostics.Tracing.EventSource"/>
/// is wired to <see cref="ExporterHealthMonitor.RecordFailure(string)"/>
/// before the first export attempt. The OTel SDK exposes no public success
/// callback, so the listener also runs a 30-second timer that calls
/// <see cref="ExporterHealthMonitor.RecordSuccess"/> whenever no failure has
/// been observed in the previous tick window — the v1 absence-of-failure
/// compromise documented on the listener type. mithril#834.</para>
/// </summary>
public static class TelemetryHostExtensions
{
    private const string MithrilSourcePrefix = "Mithril.*";

    /// <summary>
    /// Wire OTLP export into <paramref name="services"/> when
    /// <paramref name="settings"/>.<see cref="TelemetrySettings.EnableOtlpExport"/>
    /// is <c>true</c>. No-op when disabled (preserves the
    /// zero-overhead-when-off contract).
    /// </summary>
    /// <param name="services">DI service collection.</param>
    /// <param name="settings">
    /// The persisted telemetry settings instance. Pass the same reference
    /// you've registered with <c>AddMithrilVersionedSettings&lt;TelemetrySettings&gt;</c>.
    /// Restart-required fields (endpoint, headers, protocol, service-name) are
    /// captured once here at registration time. Live fields (TagExports) are read
    /// via <see cref="IOptionsMonitor{T}"/> which resolves the DI singleton on
    /// every CurrentValue read — so UI mutations on the singleton flow through to
    /// the scrubber without restart, regardless of which instance was passed to
    /// this method.
    /// </param>
    /// <param name="loggerFactory">
    /// Optional logger factory; when supplied, a logger under category
    /// <c>"Telemetry.Otlp"</c> reports corrupted DPAPI header blobs and other
    /// wiring-time anomalies. Pass <c>null</c> to swallow such warnings.
    /// </param>
    /// <returns><paramref name="services"/> for chaining.</returns>
    public static IServiceCollection AddMithrilOtlpExport(
        this IServiceCollection services,
        TelemetrySettings settings,
        ILoggerFactory? loggerFactory = null)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(settings);

        if (!settings.EnableOtlpExport)
        {
            return services;
        }

        var logger = loggerFactory?.CreateLogger("Telemetry.Otlp");

        // Bridge: IOptionsMonitor<TelemetrySettings>.CurrentValue resolves to the
        // host's singleton at request time so per-span TagExports reads see UI
        // mutations live. Capturing `settings` into a closure here would alias
        // whichever instance the caller passed in — which under
        // AddMithrilVersionedSettings<T> may be a different singleton than the
        // one the host eventually builds (e.g. when the caller resolved settings
        // from a temp ServiceProvider for the EnableOtlpExport gating decision).
        // Resolve through sp instead so the scrubber always sees the live
        // host-singleton dictionary. The monitor subscribes to the singleton's
        // PropertyChanged so OnChange listeners actually fire on the settings-UI
        // save / in-place-mutation path (mithril#833). Endpoint/headers/protocol/
        // service-name remain restart-required regardless — the OTel exporter
        // bakes them at provider-build time and never re-reads (see the
        // hot-reload note in this type's XML doc and NotifyPropertyChangedOptionsMonitor).
        services.AddSingleton<IOptionsMonitor<TelemetrySettings>>(sp =>
            new NotifyPropertyChangedOptionsMonitor<TelemetrySettings>(sp.GetRequiredService<TelemetrySettings>()));

        // Scrubber graph. Process-wide singletons; the processor references
        // the catalog + redactor + observer + settings monitor.
        services.AddSingleton<TagCatalog>();
        services.AddSingleton<NewlySeenTagsObserver>();
        services.AddSingleton<HeaderValueProtection>(); // Consumed by Task 13 settings UI.
        services.AddSingleton<ExporterHealthMonitor>();
        services.AddSingleton<OtlpExporterEventListener>();
        // Eager start: the listener subscribes to the OTel exporter EventSource
        // in its constructor, and we need it alive before the first export
        // attempt so startup-time failures (DNS, TLS, corrupted endpoint) are
        // captured. Hosted-service ordering is too coarse; resolve on host
        // start via IHostedService bridged below.
        services.AddHostedService<OtlpExporterEventListenerStarter>();
        services.AddSingleton(sp =>
        {
            // Active character read lazily on each scrub call so character
            // switches are picked up without rebuilding the redactor. The
            // character service may not be registered in pure test hosts;
            // resolving via GetService keeps that contract optional.
            var sp2 = sp;
            string? GetActiveCharacter()
            {
                try
                {
                    return sp2.GetService<IActiveCharacterService>()?.ActiveCharacterName;
                }
                catch
                {
                    return null;
                }
            }

            var userProfile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            return new ValueRedactor(GetActiveCharacter, userProfile, localAppData);
        });
        services.AddSingleton<AllowlistAndRedactionProcessor>();
        services.AddSingleton<ValueRedactionOnlyProcessor>();
        services.AddSingleton<LogScrubbingProcessor>();
        services.AddSingleton<LogValueRedactionOnlyProcessor>();

        // Resource attributes shared across traces, metrics, logs.
        var serviceInstanceId = Guid.NewGuid().ToString("D");
        var serviceVersion = ResolveServiceVersion();
        var serviceName = string.IsNullOrWhiteSpace(settings.ServiceName)
            ? "mithril"
            : settings.ServiceName;

        void ConfigureResource(ResourceBuilder rb) => rb.AddService(
            serviceName: serviceName,
            serviceNamespace: "projectgorgon",
            serviceVersion: serviceVersion,
            serviceInstanceId: serviceInstanceId);

        // Snapshot the scrubbed headers once. Corrupted DPAPI blobs are
        // dropped with a warning so a single bad row doesn't tear OTLP export.
        var headerString = BuildHeadersString(settings, logger);

        services.AddOpenTelemetry()
            .ConfigureResource(ConfigureResource)
            .WithTracing(tb =>
            {
                tb.AddSource(MithrilSourcePrefix)
                  .AddHttpClientInstrumentation();
                // TrustEndpoint chooses which processor runs on the tracing pipeline:
                // off (default) → full allowlist + redactor; on → redactor only.
                // The choice is restart-required because it's captured here at
                // AddProcessor registration time. See mithril#840.
                if (settings.TrustEndpoint)
                {
                    tb.AddProcessor<ValueRedactionOnlyProcessor>();
                }
                else
                {
                    tb.AddProcessor<AllowlistAndRedactionProcessor>();
                }
                tb.AddOtlpExporter(opts => ConfigureOtlp(opts, settings, headerString));
            })
            .WithMetrics(mb =>
            {
                mb.AddMeter(MithrilSourcePrefix);

                // Metric symmetry with the tracing scrubber (mithril#841):
                // when TrustEndpoint is off, the catalog allowlist is applied
                // as a view filter on the Mithril.* meters so Sensitive-
                // classified dimensions get dropped at aggregation time
                // (keep the metric, drop the bad tag). TagExports is read once
                // per instrument when the view callback first runs — see
                // MetricTagAllowlistView's XML doc for the restart-required
                // caveat.
                if (!settings.TrustEndpoint)
                {
                    ((IDeferredMeterProviderBuilder)mb).Configure((sp, builder) =>
                    {
                        var catalog = sp.GetRequiredService<TagCatalog>();
                        var monitor = sp.GetRequiredService<IOptionsMonitor<TelemetrySettings>>();
                        MetricTagAllowlistView.AddCatalogAllowlistView(builder, catalog, monitor);
                    });
                }

                mb.AddOtlpExporter(opts => ConfigureOtlp(opts, settings, headerString));
            });

        services.AddLogging(lb =>
        {
            lb.AddOpenTelemetry(o =>
            {
                o.SetResourceBuilder(ResourceBuilder.CreateDefault().AddService(
                    serviceName: serviceName,
                    serviceNamespace: "projectgorgon",
                    serviceVersion: serviceVersion,
                    serviceInstanceId: serviceInstanceId));
                o.IncludeFormattedMessage = true;
                o.IncludeScopes = true;

                // Log symmetry with the tracing scrubber (mithril#841): the
                // same TrustEndpoint gate that selects between the allowlist
                // and redactor-only processors on the tracing side selects
                // between the log analogues here.
                if (settings.TrustEndpoint)
                {
                    o.AddProcessor(sp => sp.GetRequiredService<LogValueRedactionOnlyProcessor>());
                }
                else
                {
                    o.AddProcessor(sp => sp.GetRequiredService<LogScrubbingProcessor>());
                }

                o.AddOtlpExporter(opts => ConfigureOtlp(opts, settings, headerString));
            });
        });

        return services;
    }

    private static void ConfigureOtlp(
        OtlpExporterOptions opts,
        TelemetrySettings settings,
        string headerString)
    {
        if (Uri.TryCreate(settings.Endpoint, UriKind.Absolute, out var uri))
        {
            opts.Endpoint = uri;
        }
        // If parsing fails, leave the OTel default endpoint in place rather
        // than throw at registration — the exporter will fail to connect at
        // runtime and surface via the diagnostics log / ExporterHealthMonitor.

        opts.Protocol = settings.Protocol switch
        {
            OtlpProtocol.Grpc => OtlpExportProtocol.Grpc,
            OtlpProtocol.HttpProtobuf => OtlpExportProtocol.HttpProtobuf,
            _ => OtlpExportProtocol.HttpProtobuf,
        };

        if (!string.IsNullOrEmpty(headerString))
        {
            opts.Headers = headerString;
        }
    }

    /// <summary>
    /// Unwrap every DPAPI-protected header value, drop entries whose unwrap
    /// fails, and format as the OTel-expected <c>key1=value1,key2=value2</c>
    /// string. Corrupted blobs are logged at Warning level and skipped — a
    /// single bad row should not block export of the remaining headers.
    /// </summary>
    // TODO: percent-encode `,` / `=` / whitespace in values per the OTLP
    // exporter spec. Real-world API keys (Seq, Honeycomb, Grafana Cloud) are
    // URL-safe in practice, so this rarely bites — but a hand-pasted bearer
    // token containing `=` will silently corrupt the headers map. Track as a
    // follow-up if it surfaces.
    private static string BuildHeadersString(TelemetrySettings settings, ILogger? logger)
    {
        if (settings.Headers.Count == 0)
        {
            return string.Empty;
        }

        var protection = new HeaderValueProtection();
        var sb = new StringBuilder();
        var first = true;
        foreach (var kv in settings.Headers)
        {
            var unwrapped = protection.Unprotect(kv.Value);
            if (unwrapped is null)
            {
                logger?.LogWarning(
                    "Telemetry header '{HeaderName}' has a corrupted DPAPI blob and was skipped. " +
                    "Re-enter the value in the telemetry settings UI.",
                    kv.Key);
                continue;
            }
            if (!first) sb.Append(',');
            sb.Append(kv.Key).Append('=').Append(unwrapped);
            first = false;
        }
        return sb.ToString();
    }

    /// <summary>
    /// Resolve the assembly informational version to populate
    /// <c>service.version</c>. Prefers the entry assembly (typically
    /// <c>Mithril.Shell</c>) so the user-facing version matches the running
    /// shell binary; falls back to this assembly's version when the entry
    /// assembly is null (e.g. test host).
    /// </summary>
    private static string ResolveServiceVersion()
    {
        var asm = Assembly.GetEntryAssembly() ?? typeof(TelemetryHostExtensions).Assembly;
        return asm.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
            ?? asm.GetName().Version?.ToString()
            ?? "0.0.0";
    }

    /// <summary>
    /// Hosted-service shim that resolves the <see cref="OtlpExporterEventListener"/>
    /// singleton on host start so the listener subscribes to the OTel exporter
    /// EventSource <em>before</em> the first export attempt. The host's hosted-
    /// service collection is started in registration order, and this entry is
    /// registered after the OTel <see cref="OpenTelemetry.Trace.TracerProvider"/>
    /// hosted services — but EventListener subscription is process-global and
    /// completes synchronously in the constructor, so order here only needs to
    /// be "before the first batch flush", which is satisfied at host start.
    /// <para>The starter <em>does not</em> dispose the listener in
    /// <see cref="StopAsync"/>. The listener is registered as a DI singleton
    /// (<see cref="ServiceLifetime.Singleton"/>) so the host's
    /// <see cref="IServiceProvider"/> disposes it during host teardown.
    /// Disposing here too would invoke
    /// <see cref="OtlpExporterEventListener.Dispose"/> twice — benign today
    /// (BCL <see cref="System.Threading.Timer"/> and
    /// <see cref="System.Diagnostics.Tracing.EventListener"/> are idempotent)
    /// but the contract isn't guaranteed by either API, and a subclass that
    /// adds state in <see cref="OtlpExporterEventListener.Dispose"/> would
    /// break it silently. Let DI own the lifecycle.</para>
    /// </summary>
    private sealed class OtlpExporterEventListenerStarter(OtlpExporterEventListener listener)
        : Microsoft.Extensions.Hosting.IHostedService
    {
        public Task StartAsync(CancellationToken cancellationToken)
        {
            // Touch the listener so DI builds the singleton (the ctor self-subscribes).
            _ = listener;
            return Task.CompletedTask;
        }

        public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
    }
}
