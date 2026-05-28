using System;
using System.Collections.Generic;
using System.Diagnostics.Metrics;
using Microsoft.Extensions.Options;
using Mithril.Shared.Telemetry.Catalog;
using Mithril.Shared.Telemetry.Settings;
using OpenTelemetry.Metrics;

namespace Mithril.Shared.Telemetry.Processing;

/// <summary>
/// Metrics-pipeline equivalent of <see cref="AllowlistAndRedactionProcessor"/>
/// — applies the catalog allowlist + user export contract to metric tag
/// dimensions via OpenTelemetry's view API
/// (<see cref="MeterProviderBuilder.AddView(MeterProviderBuilder, Func{Instrument, MetricStreamConfiguration})"/>).
///
/// For every instrument emitted by a <c>Mithril.*</c> meter, the view returns
/// a <see cref="MetricStreamConfiguration"/> whose
/// <see cref="MetricStreamConfiguration.TagKeys"/> is the subset of
/// catalog-known keys that are <em>exported</em> after applying the same
/// three-layer model used on the span side:
/// <list type="number">
/// <item>The catalog entry must exist (unknown keys are dropped fail-closed).</item>
/// <item>If the user has an explicit <see cref="TelemetrySettings.TagExports"/>
/// override, it wins.</item>
/// <item>Otherwise the catalog descriptor's default applies (Safe / Identifying
/// default on, Sensitive default off).</item>
/// </list>
///
/// <para><strong>Restart-required.</strong> The view callback is invoked by
/// the OTel SDK once per <see cref="Instrument"/> when the instrument is
/// first observed, and the resulting <see cref="MetricStreamConfiguration.TagKeys"/>
/// array is captured. The catalog is process-stable, so a catalog-only change
/// is moot; the user-facing implication is that toggling a metric-relevant
/// tag in the settings UI's chip cloud requires a restart for the metric side
/// to honour it. The span-side processor still re-reads
/// <c>CurrentValue.TagExports</c> per span and remains live. This asymmetry
/// is called out in <see cref="Hosting.TelemetryHostExtensions.AddMithrilOtlpExport"/>'s
/// XML doc.</para>
///
/// <para><strong>Newly-seen note.</strong> The view runs before any
/// measurement reaches a metric reader, so it has no visibility into
/// instrument-time tag KEYS that are not declared on the
/// <see cref="Instrument"/> at construction. Mithril instruments declare their
/// dimensions via <c>KeyValuePair</c> args at record time — the view callback
/// only sees the <c>Instrument.Name</c> / <c>Meter</c> identity. Unknown keys
/// emitted at record time are dropped silently by the OTel SDK when the view
/// supplies a <c>TagKeys</c> array (the SDK only retains tags whose keys are
/// in the array), so "newly seen" surfacing is a span/log responsibility — the
/// metric side has no observable hook for it.</para>
/// </summary>
internal static class MetricTagAllowlistView
{
    /// <summary>
    /// Register the catalog-driven view on <paramref name="builder"/> so every
    /// metric instrument's exported tag-key set is restricted to the catalog
    /// allowlist intersected with <see cref="TelemetrySettings.TagExports"/>
    /// at view-config time.
    /// </summary>
    /// <param name="builder">Meter provider builder to configure.</param>
    /// <param name="catalog">Frozen union of known tag descriptors.</param>
    /// <param name="settings">Live settings monitor — read once per instrument observation.</param>
    public static void AddCatalogAllowlistView(
        MeterProviderBuilder builder,
        TagCatalog catalog,
        IOptionsMonitor<TelemetrySettings> settings)
    {
        builder.AddView(instrument =>
        {
            // Only apply our filter to Mithril.* meters. Auto-instrumentation
            // meters (Http, Runtime, etc.) carry their own tag vocabulary that
            // our catalog has no opinion on; returning null leaves them alone.
            var meterName = instrument.Meter?.Name;
            if (meterName is null || !meterName.StartsWith("Mithril.", StringComparison.Ordinal))
            {
                return null!;
            }

            var current = settings.CurrentValue;
            var allowed = new List<string>(capacity: 8);
            foreach (var descriptor in catalog.Descriptors)
            {
                var userOverride = current.TagExports.TryGetValue(descriptor.Key, out var v) ? (bool?)v : null;
                var exported = userOverride ?? descriptor.DefaultExported;
                if (exported)
                {
                    allowed.Add(descriptor.Key);
                }
            }

            return new MetricStreamConfiguration
            {
                TagKeys = allowed.ToArray(),
            };
        });
    }
}
