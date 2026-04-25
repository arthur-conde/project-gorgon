using System.Text.RegularExpressions;
using Mithril.Shared.Logging;
using Legolas.Domain;

namespace Legolas.Services;

public sealed partial class ChatLogParser : IChatLogParser
{
    [GeneratedRegex(@"\[Status\] The (?<name>.+?) is (?<a>\d+)m (?<aDir>north|south|east|west) and (?<b>\d+)m (?<bDir>north|south|east|west)\b",
        RegexOptions.Compiled | RegexOptions.CultureInvariant | RegexOptions.IgnoreCase)]
    private static partial Regex SurveyRegex();

    [GeneratedRegex(@"\[Status\]\s+(?<name>.+?)\s+(?:x(?<count>\d+)\s+)?collected!",
        RegexOptions.Compiled | RegexOptions.CultureInvariant)]
    private static partial Regex CollectRegex();

    [GeneratedRegex(@"The treasure is (?<dist>\d+) metres from here",
        RegexOptions.Compiled | RegexOptions.CultureInvariant)]
    private static partial Regex MotherlodeRegex();

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

        var collectMatch = CollectRegex().Match(line);
        if (collectMatch.Success)
        {
            var count = 1;
            if (collectMatch.Groups["count"].Success
                && int.TryParse(collectMatch.Groups["count"].ValueSpan, out var n))
            {
                count = n;
            }
            return new ItemCollected(
                timestamp,
                collectMatch.Groups["name"].Value.Trim(),
                count);
        }

        var motherlodeMatch = MotherlodeRegex().Match(line);
        if (motherlodeMatch.Success
            && int.TryParse(motherlodeMatch.Groups["dist"].ValueSpan, out var mlDist))
        {
            return new MotherlodeDistance(timestamp, mlDist);
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
