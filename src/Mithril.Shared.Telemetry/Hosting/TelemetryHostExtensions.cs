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
///   <see cref="ExporterHealthMonitor"/>, <see cref="AllowlistAndRedactionProcessor"/>.</item>
/// <item>OpenTelemetry tracing for every <c>Mithril.*</c>
///   <see cref="System.Diagnostics.ActivitySource"/> with HTTP-client
///   auto-instrumentation and OTLP export.</item>
/// <item>OpenTelemetry metrics for every <c>Mithril.*</c>
///   <see cref="System.Diagnostics.Metrics.Meter"/> with OTLP export.</item>
/// <item>OpenTelemetry logs bridge with OTLP export, sharing the resource
///   attribute set with traces and metrics.</item>
/// </list>
///
/// <para><strong>Hot-reload — partial in v1.</strong>
/// <see cref="TelemetrySettings.TagExports"/> mutations on the singleton
/// flow through to the scrubber live (per-span read of
/// <c>IOptionsMonitor.CurrentValue.TagExports</c>) — the settings UI can
/// toggle a tag and the next exported span honours it. Endpoint, headers,
/// protocol, and service-name changes still require a process restart in
/// v1: the OTel SDK <c>AddOtlpExporter</c> callback captures those once at
/// registration, and threading per-field <c>OnChange</c> notifications
/// through the exporter wrapper is non-trivial. Filed as a follow-up
/// enhancement.</para>
///
/// <para><strong>EventSource listener deferred (v1).</strong> The
/// <see cref="ExporterHealthMonitor"/> is registered in DI so the settings UI
/// can bind to it, but the
/// <see cref="ExporterHealthMonitor.RecordFailure(string)"/> plumbing fed by
/// the OTel SDK's internal EventSource is left for a follow-up — v1 users can
/// read OTLP errors from the on-disk diagnostics .json log. The status line
/// will show "no activity yet" until then, which is honest.</para>
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
        // host-singleton dictionary. OnChange notifications are a v1 no-op — the
        // scrubber polls CurrentValue, doesn't subscribe.
        services.AddSingleton<IOptionsMonitor<TelemetrySettings>>(sp =>
            new SingletonOptionsMonitor<TelemetrySettings>(sp.GetRequiredService<TelemetrySettings>()));

        // Scrubber graph. Process-wide singletons; the processor references
        // the catalog + redactor + observer + settings monitor.
        services.AddSingleton<TagCatalog>();
        services.AddSingleton<NewlySeenTagsObserver>();
        services.AddSingleton<HeaderValueProtection>(); // Consumed by Task 13 settings UI.
        services.AddSingleton<ExporterHealthMonitor>();
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
                  .AddHttpClientInstrumentation()
                  .AddProcessor<AllowlistAndRedactionProcessor>()
                  .AddOtlpExporter(opts => ConfigureOtlp(opts, settings, headerString));
            })
            .WithMetrics(mb =>
            {
                mb.AddMeter(MithrilSourcePrefix)
                  .AddOtlpExporter(opts => ConfigureOtlp(opts, settings, headerString));
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
    /// IOptionsMonitor shim that returns the DI singleton on every CurrentValue
    /// read so in-place mutations (TagExports add/remove) are picked up per
    /// scrub. OnChange notification is a no-op for v1 — the scrubber polls
    /// CurrentValue, doesn't subscribe. The full OnChange path lands with the
    /// IOptionsMonitor hot-reload follow-up (see XML doc on AddMithrilOtlpExport).
    /// </summary>
    private sealed class SingletonOptionsMonitor<T>(T instance) : IOptionsMonitor<T> where T : class
    {
        public T CurrentValue => instance;
        public T Get(string? name) => instance;
        public IDisposable OnChange(Action<T, string?> listener) => NoSubscription.Instance;

        private sealed class NoSubscription : IDisposable
        {
            public static readonly NoSubscription Instance = new();
            public void Dispose() { }
        }
    }
}
