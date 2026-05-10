using System.Text.RegularExpressions;
using Mithril.Shared.Logging;

namespace Gandalf.Parsing;

/// <summary>
/// Parses the <c>ProcessScreenText(ErrorMessage, ...)</c> rejection emitted
/// when the player tries to milk a cow that's still on cooldown. Wiki sample
/// (#181):
/// <c>ProcessScreenText(ErrorMessage, "You've already milked Bessie in the past hour.")</c>
///
/// Distinct from <see cref="ChestRejectionParser"/> in three ways: different
/// log channel (<c>ErrorMessage</c> vs <c>GeneralInfo</c>), different verb
/// (<c>milked</c> vs <c>looted</c>), and a relative-past grammar that gives
/// only the cooldown bound (`"in the past hour"`) instead of the chest's
/// forward-looking remaining time. The duration is correlated to the
/// in-flight bracket the same way the chest parser does — the message
/// carries the cow's friendly name (<c>"Bessie"</c>) but not its internal
/// name (<c>"Cow_Bessie"</c>), and the bracket already knows the latter.
///
/// Singular form (<c>"in the past hour"</c>) is treated as 1 of the unit;
/// numeric form (<c>"in the past 30 minutes"</c>) extracts the value. Live
/// captures so far have only the singular variant; the numeric branch is
/// defensive — see #181 verification owed.
/// </summary>
public sealed partial class MilkingRejectionParser : ILogParser
{
    [GeneratedRegex(
        """ProcessScreenText\(ErrorMessage,\s*"You've already milked\s+\S+\s+in the past\s+(?:(?<value>\d+)\s+)?(?<unit>minute|hour|day)s?\.\s*"\)""",
        RegexOptions.CultureInvariant)]
    private static partial Regex RejectionRx();

    public LogEvent? TryParse(string line, DateTime timestamp)
    {
        if (!line.Contains("You've already milked", StringComparison.Ordinal))
            return null;

        var m = RejectionRx().Match(line);
        if (!m.Success) return null;

        var value = m.Groups["value"].Success && int.TryParse(m.Groups["value"].Value, out var v)
            ? v
            : 1; // "in the past hour" → 1 hour; numeric form has the explicit count
        var unit = m.Groups["unit"].Value;
        var duration = unit switch
        {
            "minute" => TimeSpan.FromMinutes(value),
            "hour" => TimeSpan.FromHours(value),
            "day" => TimeSpan.FromDays(value),
            _ => TimeSpan.Zero,
        };
        if (duration == TimeSpan.Zero) return null;

        // ChestInternalName is filled in by the bracket tracker at correlation time,
        // mirroring the chest path.
        return new ChestCooldownObservedEvent(timestamp, ChestInternalName: "", duration);
    }
}
