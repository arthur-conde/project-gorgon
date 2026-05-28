using System;
using System.Collections.Generic;
using System.Reflection;
using System.Text;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
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
/// <para><strong>Hot-reload deferred (v1).</strong> Endpoint / headers /
/// service-name changes require a process restart in v1. The OTel SDK
/// <c>AddOtlpExporter</c> callback captures settings at registration time,
/// and threading <c>IOptionsMonitor&lt;TelemetrySettings&gt;</c> through is
/// non-trivial. Filed as a follow-up enhancement; the user can iterate on a
/// local Seq config by restarting between changes.</para>
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
    /// Telemetry settings instance, loaded by the caller (typically via
    /// <c>AddMithrilVersionedSettings&lt;TelemetrySettings&gt;</c>). The
    /// settings are read once at registration; see remarks on
    /// <see cref="TelemetryHostExtensions"/> for the deferred hot-reload note.
    /// </param>
    /// <param name="logger">
    /// Optional logger used to report corrupted DPAPI header blobs and other
    /// wiring-time anomalies. Pass <c>null</c> to swallow such warnings.
    /// </param>
    /// <returns><paramref name="services"/> for chaining.</returns>
    public static IServiceCollection AddMithrilOtlpExport(
        this IServiceCollection services,
        TelemetrySettings settings,
        ILogger? logger = null)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(settings);

        if (!settings.EnableOtlpExport)
        {
            return services;
        }

        // Bind a snapshot of TelemetrySettings into IOptionsMonitor so the
        // scrubber processor can resolve it via DI. In v1 the snapshot is
        // captured once at registration; hot-reload is a deferred enhancement
        // (see remarks above). The caller may also register their own
        // IOptionsMonitor<TelemetrySettings> upstream — Configure here is
        // additive, so a richer binding wins last-writer.
        services.AddOptions<TelemetrySettings>().Configure(o => CopySettings(settings, o));

        // Scrubber graph. Process-wide singletons; the processor references
        // the catalog + redactor + observer + settings monitor.
        services.AddSingleton<TagCatalog>();
        services.AddSingleton<NewlySeenTagsObserver>();
        services.AddSingleton<HeaderValueProtection>();
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
    /// Copy the user-supplied settings instance into the IOptions-managed
    /// instance. Done field-by-field rather than reference assignment so the
    /// caller-held instance and the IOptions-resolved instance don't alias —
    /// surprises from in-place edits propagating into running pipelines are
    /// deferred to the hot-reload follow-up.
    /// </summary>
    private static void CopySettings(TelemetrySettings source, TelemetrySettings target)
    {
        target.SchemaVersion = source.SchemaVersion;
        target.EnableOtlpExport = source.EnableOtlpExport;
        target.Endpoint = source.Endpoint;
        target.Protocol = source.Protocol;
        target.ServiceName = source.ServiceName;
        target.Headers = new Dictionary<string, string>(source.Headers);
        target.TagExports = new Dictionary<string, bool>(source.TagExports);
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
}
