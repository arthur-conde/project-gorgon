using System.Collections.Generic;
using Microsoft.Extensions.Options;
using Mithril.Shared.Telemetry.Catalog;
using Mithril.Shared.Telemetry.Settings;
using OpenTelemetry;
using OpenTelemetry.Logs;

namespace Mithril.Shared.Telemetry.Processing;

/// <summary>
/// OpenTelemetry <see cref="BaseProcessor{T}"/> for the logs pipeline that
/// mirrors <see cref="AllowlistAndRedactionProcessor"/> from the tracing
/// pipeline: filters <see cref="LogRecord.Attributes"/> through the same
/// three-layer model (catalog membership → user export contract → value
/// redaction) and additionally redacts the formatted message body and
/// <see cref="LogRecord.Body"/> with <see cref="ValueRedactor"/>.
///
/// Unknown attribute keys are dropped and notified to
/// <see cref="NewlySeenTagsObserver"/> so the settings UI surfaces them
/// for the user to promote — same fail-closed semantics as the span side.
///
/// Runs on the OTel log export pipeline thread (BatchProcessor consumer),
/// so the operations on each <see cref="LogRecord"/> must be O(attribute-count)
/// and allocation-frugal — we rebuild the attribute list only when at least
/// one drop or value mutation has been observed, to keep the no-change path
/// alloc-free.
///
/// mithril#841 — symmetry with the span scrubber.
/// </summary>
public sealed class LogScrubbingProcessor : BaseProcessor<LogRecord>
{
    private readonly TagCatalog _catalog;
    private readonly IOptionsMonitor<TelemetrySettings> _settings;
    private readonly ValueRedactor _redactor;
    private readonly NewlySeenTagsObserver _newlySeen;

    /// <summary>
    /// Creates a log processor over the supplied catalog, settings monitor,
    /// value redactor, and newly-seen-key observer.
    /// </summary>
    public LogScrubbingProcessor(
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
    public override void OnEnd(LogRecord data)
    {
        var settings = _settings.CurrentValue;

        var attributes = data.Attributes;
        if (attributes is not null && attributes.Count > 0)
        {
            List<KeyValuePair<string, object?>>? rewritten = null;
            for (var i = 0; i < attributes.Count; i++)
            {
                var kv = attributes[i];
                var key = kv.Key;

                // {OriginalFormat} is MEL's message-template carrier — the OTel
                // logs bridge sets LogRecord.Body from it when present, and the
                // tracing-side catalog has no entry for it. Treat as a
                // structural attribute (kept, but redacted as a string value).
                if (key == "{OriginalFormat}")
                {
                    var maybeRedacted = MaybeRedact(kv.Value);
                    if (!ReferenceEquals(maybeRedacted, kv.Value))
                    {
                        rewritten ??= CopyUpTo(attributes, i);
                        rewritten.Add(new KeyValuePair<string, object?>(key, maybeRedacted));
                    }
                    else
                    {
                        rewritten?.Add(kv);
                    }
                    continue;
                }

                if (!_catalog.TryGetDescriptor(key, out var descriptor))
                {
                    _newlySeen.Note(key);
                    rewritten ??= CopyUpTo(attributes, i);
                    continue; // drop — do not append
                }

                var userOverride = settings.TagExports.TryGetValue(key, out var v) ? (bool?)v : null;
                var exported = userOverride ?? descriptor!.DefaultExported;
                if (!exported)
                {
                    rewritten ??= CopyUpTo(attributes, i);
                    continue; // drop
                }

                var redactedValue = MaybeRedact(kv.Value);
                if (!ReferenceEquals(redactedValue, kv.Value))
                {
                    rewritten ??= CopyUpTo(attributes, i);
                    rewritten.Add(new KeyValuePair<string, object?>(key, redactedValue));
                }
                else
                {
                    rewritten?.Add(kv);
                }
            }

            if (rewritten is not null)
            {
                data.Attributes = rewritten;
            }
        }

        // Message body + Body slot — apply path/character redaction to the
        // formatted text. The body is what backends typically render verbatim
        // and is the most likely surface for an accidental path / character
        // name leak (e.g. ILogger.LogWarning("Loaded {File} from {Path}", …)
        // where the formatted message inlines a %LocalAppData% path).
        if (data.FormattedMessage is { } fm)
        {
            var redacted = _redactor.Redact(fm);
            if (!ReferenceEquals(redacted, fm))
            {
                data.FormattedMessage = redacted;
            }
        }

        if (data.Body is { } body)
        {
            var redactedBody = _redactor.Redact(body);
            if (!ReferenceEquals(redactedBody, body))
            {
                data.Body = redactedBody;
            }
        }
    }

    private object? MaybeRedact(object? value)
    {
        if (value is string s)
        {
            return _redactor.Redact(s);
        }
        return value;
    }

    private static List<KeyValuePair<string, object?>> CopyUpTo(
        IReadOnlyList<KeyValuePair<string, object?>> source,
        int upToExclusive)
    {
        var list = new List<KeyValuePair<string, object?>>(source.Count);
        for (var i = 0; i < upToExclusive; i++)
        {
            list.Add(source[i]);
        }
        return list;
    }
}
