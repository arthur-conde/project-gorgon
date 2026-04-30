using System.Text.RegularExpressions;
using Mithril.Shared.Logging;

namespace Gandalf.Parsing;

/// <summary>
/// Parses the <c>ProcessScreenText</c> rejection emitted when the player tries
/// to re-loot a chest still on cooldown. Per the wiki sample:
/// <c>ProcessScreenText(GeneralInfo, "You've already looted this chest! (It will refill 3 hours after you looted it.)")</c>
///
/// The duration is the only authoritative source — chests don't expose their
/// cooldown at first-loot time. We can't know the chest's *internal name* from
/// this line alone (the rejection screen text doesn't carry it), so the
/// ingestion service correlates this event with the most-recent
/// <see cref="ChestInteractionEvent"/> in the same bracket.
/// </summary>
public sealed partial class ChestRejectionParser : ILogParser
{
    [GeneratedRegex(
        """ProcessScreenText\(GeneralInfo,\s*"You've already looted this chest! \(It will refill (?<value>\d+)\s*(?<unit>minute|hour|day)s?\s*after you looted it\.\)"\)""",
        RegexOptions.CultureInvariant)]
    private static partial Regex RejectionRx();

    public LogEvent? TryParse(string line, DateTime timestamp)
    {
        if (!line.Contains("You've already looted this chest!", StringComparison.Ordinal))
            return null;

        var m = RejectionRx().Match(line);
        if (!m.Success) return null;

        if (!int.TryParse(m.Groups["value"].Value, out var value)) return null;
        var unit = m.Groups["unit"].Value;
        var duration = unit switch
        {
            "minute" => TimeSpan.FromMinutes(value),
            "hour" => TimeSpan.FromHours(value),
            "day" => TimeSpan.FromDays(value),
            _ => TimeSpan.Zero,
        };
        if (duration == TimeSpan.Zero) return null;

        // ChestInternalName is filled in by the ingestion service at correlation time.
        return new ChestCooldownObservedEvent(timestamp, ChestInternalName: "", duration);
    }
}
