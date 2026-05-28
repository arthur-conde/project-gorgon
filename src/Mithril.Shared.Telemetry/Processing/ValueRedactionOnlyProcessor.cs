using System.Diagnostics;
using System.Linq;
using OpenTelemetry;

namespace Mithril.Shared.Telemetry.Processing;

/// <summary>
/// OpenTelemetry <see cref="BaseProcessor{T}"/> registered in place of
/// <see cref="AllowlistAndRedactionProcessor"/> when
/// <see cref="Settings.TelemetrySettings.TrustEndpoint"/> is <c>true</c>.
///
/// Skips the catalog / allowlist / user-override gate entirely — every producer
/// tag flows to the OTLP destination — but still runs <see cref="ValueRedactor"/>
/// across string tag values as belt-and-suspenders against accidental path-prefix
/// or active-character-name leaks. The redactor is cheap, and prevents the dumbest
/// accidental-screenshot-paste leak class without breaking the
/// "I want to see what's actually being emitted" intent that motivates trust mode.
///
/// See mithril#840.
/// </summary>
public sealed class ValueRedactionOnlyProcessor : BaseProcessor<Activity>
{
    private readonly ValueRedactor _redactor;

    public ValueRedactionOnlyProcessor(ValueRedactor redactor)
    {
        _redactor = redactor;
    }

    /// <inheritdoc />
    public override void OnEnd(Activity activity)
    {
        var keys = activity.TagObjects.Select(kv => kv.Key).ToList();
        foreach (var key in keys)
        {
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
