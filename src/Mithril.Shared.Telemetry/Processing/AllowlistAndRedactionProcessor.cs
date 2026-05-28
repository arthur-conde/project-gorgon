using System.Diagnostics;
using System.Linq;
using Microsoft.Extensions.Options;
using Mithril.Shared.Telemetry.Catalog;
using Mithril.Shared.Telemetry.Settings;
using OpenTelemetry;

namespace Mithril.Shared.Telemetry.Processing;

/// <summary>
/// OpenTelemetry <see cref="BaseProcessor{T}"/> that filters span tags
/// through the three-layer model described in mithril#815:
/// (1) catalog membership, (2) user export contract from settings,
/// (3) value redaction for path prefixes + active character name.
///
/// Unknown tag keys are dropped and notified to
/// <see cref="NewlySeenTagsObserver"/> so the settings UI surfaces them
/// for the user to review. Fail-closed: new producer tags are NOT exported
/// until explicitly enabled.
///
/// Runs on the OTel export pipeline thread (BatchProcessor consumer), so
/// the operations on each Activity must be O(tag-count) and allocation-frugal.
/// Tag rewrite uses <see cref="Activity.SetTag(string, object?)"/> with the
/// value <c>null</c> to drop a key &mdash; OTel SDK exporters skip null-valued tags.
/// </summary>
public sealed class AllowlistAndRedactionProcessor : BaseProcessor<Activity>
{
    private readonly TagCatalog _catalog;
    private readonly IOptionsMonitor<TelemetrySettings> _settings;
    private readonly ValueRedactor _redactor;
    private readonly NewlySeenTagsObserver _newlySeen;

    /// <summary>
    /// Creates a processor over the supplied catalog, settings monitor, value
    /// redactor, and newly-seen-key observer.
    /// </summary>
    /// <param name="catalog">Frozen union of allowed tag keys.</param>
    /// <param name="settings">Live settings monitor — <c>CurrentValue.TagExports</c> is consulted per span.</param>
    /// <param name="redactor">String value redactor applied to surviving tag values.</param>
    /// <param name="newlySeen">Observer notified on first sight of an unknown key.</param>
    public AllowlistAndRedactionProcessor(
        TagCatalog catalog,
        IOptionsMonitor<TelemetrySettings> settings,
        ValueRedactor redactor,
        NewlySeenTagsObserver newlySeen)
    {
        _catalog = catalog;
        _settings = settings;
        _redactor = redactor;
        _newlySeen = newlySeen;
    }

    /// <inheritdoc />
    public override void OnEnd(Activity activity)
    {
        var settings = _settings.CurrentValue;
        // Snapshot the tag list - we'll mutate via SetTag(key, null) inside the loop.
        var keys = activity.TagObjects.Select(kv => kv.Key).ToList();
        foreach (var key in keys)
        {
            if (!_catalog.TryGetDescriptor(key, out var descriptor))
            {
                _newlySeen.Note(key);
                activity.SetTag(key, null);
                continue;
            }

            var userOverride = settings.TagExports.TryGetValue(key, out var v) ? (bool?)v : null;
            var exported = userOverride ?? descriptor!.DefaultExported;
            if (!exported)
            {
                activity.SetTag(key, null);
                continue;
            }

            // Allowlisted - apply value redaction to string values.
            var original = activity.GetTagItem(key);
            if (original is string s)
            {
                var redacted = _redactor.Redact(s);
                if (!ReferenceEquals(redacted, s))
                {
                    activity.SetTag(key, redacted);
                }
            }
        }
    }
}
