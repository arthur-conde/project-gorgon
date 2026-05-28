using System.Collections.Generic;
using OpenTelemetry;
using OpenTelemetry.Logs;

namespace Mithril.Shared.Telemetry.Processing;

/// <summary>
/// OpenTelemetry <see cref="BaseProcessor{T}"/> registered on the logs
/// pipeline in place of <see cref="LogScrubbingProcessor"/> when
/// <see cref="Settings.TelemetrySettings.TrustEndpoint"/> is <c>true</c>.
///
/// Skips the catalog / allowlist / user-override gate entirely — every log
/// attribute flows to the OTLP destination — but still runs
/// <see cref="ValueRedactor"/> across string attribute values and the
/// formatted message body / <see cref="LogRecord.Body"/> as belt-and-suspenders
/// against accidental path-prefix or active-character-name leaks. Mirrors
/// <see cref="ValueRedactionOnlyProcessor"/> on the tracing side. See
/// mithril#840 + mithril#841.
/// </summary>
public sealed class LogValueRedactionOnlyProcessor : BaseProcessor<LogRecord>
{
    private readonly ValueRedactor _redactor;

    public LogValueRedactionOnlyProcessor(ValueRedactor redactor)
    {
        _redactor = redactor;
    }

    /// <inheritdoc />
    public override void OnEnd(LogRecord data)
    {
        var attributes = data.Attributes;
        if (attributes is not null && attributes.Count > 0)
        {
            List<KeyValuePair<string, object?>>? rewritten = null;
            for (var i = 0; i < attributes.Count; i++)
            {
                var kv = attributes[i];
                if (kv.Value is string s)
                {
                    var redacted = _redactor.Redact(s);
                    if (!ReferenceEquals(redacted, s))
                    {
                        if (rewritten is null)
                        {
                            rewritten = new List<KeyValuePair<string, object?>>(attributes.Count);
                            for (var j = 0; j < i; j++) rewritten.Add(attributes[j]);
                        }
                        rewritten.Add(new KeyValuePair<string, object?>(kv.Key, redacted));
                        continue;
                    }
                }
                rewritten?.Add(kv);
            }

            if (rewritten is not null)
            {
                data.Attributes = rewritten;
            }
        }

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
}
