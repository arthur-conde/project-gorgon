using System.Text.RegularExpressions;
using Mithril.Shared.Logging;
using Legolas.Domain;

namespace Legolas.Services;

public sealed partial class ChatLogParser : IChatLogParser
{
    [GeneratedRegex(@"\[Status\] The (?<name>.+?) is (?<a>\d+)m (?<aDir>north|south|east|west) and (?<b>\d+)m (?<bDir>north|south|east|west)\b",
        RegexOptions.Compiled | RegexOptions.CultureInvariant | RegexOptions.IgnoreCase)]
    private static partial Regex SurveyRegex();

    // Survey collection. PG moved counts onto the "added to inventory" line, so the
    // (?:x...)? group is rarely present in real chat — keep it matched so legacy /
    // edge cases still parse. The optional "Also found Y (speed bonus!)" suffix
    // captures the second item produced by a survey speed bonus.
    [GeneratedRegex(@"\[Status\]\s+(?<name>.+?)(?:\s+x(?<count>\d+))?\s+collected!(?:\s+Also found\s+(?<bonus>.+?)\s+\(speed bonus!\))?",
        RegexOptions.Compiled | RegexOptions.CultureInvariant)]
    private static partial Regex CollectRegex();

    // "[Status] X added to inventory." or "[Status] X xN added to inventory."
    // Trailing period is matched loosely; we don't anchor end-of-line so any later
    // chat formatter quirks (e.g. trailing whitespace) still resolve.
    [GeneratedRegex(@"\[Status\]\s+(?<name>.+?)(?:\s+x(?<count>\d+))?\s+added to inventory\b",
        RegexOptions.Compiled | RegexOptions.CultureInvariant)]
    private static partial Regex InventoryAddRegex();

    // Motherlode discriminator: "The treasure is <N> meters from here" with NO
    // direction token (regular survey emits "[Status] The X is Ym DIR …", which
    // SurveyRegex requires the DIR for — the two cannot collide). PG's live line
    // uses US "meters"; accept the "metres" spelling too for robustness (#488).
    [GeneratedRegex(@"The treasure is (?<dist>\d+) met(?:er|re)s from here",
        RegexOptions.Compiled | RegexOptions.CultureInvariant)]
    private static partial Regex MotherlodeRegex();

    // Area banner: a run of asterisks then "Entering Area: <FriendlyName>".
    // The name runs to end-of-line; trim trailing whitespace via the lazy group
    // plus an explicit \s* tail so a trailing CR/space doesn't bleed into it.
    [GeneratedRegex(@"Entering Area:\s*(?<area>.+?)\s*$",
        RegexOptions.Compiled | RegexOptions.CultureInvariant)]
    private static partial Regex AreaEnteredRegex();

    public LogEvent? TryParse(string line, DateTime timestamp)
    {
        if (string.IsNullOrWhiteSpace(line))
        {
            return null;
        }

        var surveyMatch = SurveyRegex().Match(line);
        if (surveyMatch.Success
            && int.TryParse(surveyMatch.Groups["a"].ValueSpan, out var aValue)
            && int.TryParse(surveyMatch.Groups["b"].ValueSpan, out var bValue))
        {
            double east = 0, north = 0;
            ApplyComponent(surveyMatch.Groups["aDir"].Value, aValue, ref east, ref north);
            ApplyComponent(surveyMatch.Groups["bDir"].Value, bValue, ref east, ref north);
            return new SurveyDetected(
                timestamp,
                surveyMatch.Groups["name"].Value.Trim(),
                new MetreOffset(east, north));
        }

        // Try "added to inventory" before "collected!" — the regexes don't overlap
        // (different anchor phrases), but ordering keeps intent obvious.
        var addMatch = InventoryAddRegex().Match(line);
        if (addMatch.Success)
        {
            var count = 1;
            if (addMatch.Groups["count"].Success
                && int.TryParse(addMatch.Groups["count"].ValueSpan, out var addN))
            {
                count = addN;
            }
            return new ItemAddedToInventory(
                timestamp,
                addMatch.Groups["name"].Value.Trim(),
                count);
        }

        var collectMatch = CollectRegex().Match(line);
        if (collectMatch.Success)
        {
            var count = 1;
            if (collectMatch.Groups["count"].Success
                && int.TryParse(collectMatch.Groups["count"].ValueSpan, out var n))
            {
                count = n;
            }
            string? bonus = null;
            if (collectMatch.Groups["bonus"].Success)
            {
                bonus = collectMatch.Groups["bonus"].Value.Trim();
            }
            return new ItemCollected(
                timestamp,
                collectMatch.Groups["name"].Value.Trim(),
                count,
                bonus);
        }

        var motherlodeMatch = MotherlodeRegex().Match(line);
        if (motherlodeMatch.Success
            && int.TryParse(motherlodeMatch.Groups["dist"].ValueSpan, out var mlDist))
        {
            return new MotherlodeDistance(timestamp, mlDist);
        }

        var areaMatch = AreaEnteredRegex().Match(line);
        if (areaMatch.Success)
        {
            var area = areaMatch.Groups["area"].Value.Trim();
            if (!string.IsNullOrEmpty(area))
                return new AreaEntered(timestamp, area);
        }

        return new UnknownLine(timestamp, line);
    }

    private static void ApplyComponent(string direction, int value, ref double east, ref double north)
    {
        switch (direction.ToLowerInvariant())
        {
            case "east":  east  = value;  break;
            case "west":  east  = -value; break;
            case "north": north = value;  break;
            case "south": north = -value; break;
        }
    }
}
