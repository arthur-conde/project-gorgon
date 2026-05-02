using System.Globalization;
using System.Text.RegularExpressions;
using Mithril.Shared.Logging;

namespace Gandalf.Parsing;

/// <summary>
/// Parses <c>LocalPlayer: ProcessEndInteraction(id)</c> into an
/// <see cref="InteractionEndEvent"/>. Symmetric close to the existing
/// <c>ProcessEnableInteractors</c> handler in <c>LootBracketTracker</c>:
/// portals (and other quick interactions) close via this signal instead.
///
/// Live capture (#91):
/// <c>LocalPlayer: ProcessStartInteraction(-158, 8, 0, False, "Portal")</c>
/// <c>LocalPlayer: ProcessEndInteraction(-158)</c>
/// </summary>
public sealed partial class InteractionEndParser : ILogParser
{
    [GeneratedRegex(
        """LocalPlayer:\s*ProcessEndInteraction\((?<id>-?\d+)\)""",
        RegexOptions.CultureInvariant)]
    private static partial Regex EndRx();

    public LogEvent? TryParse(string line, DateTime timestamp)
    {
        if (!line.Contains("ProcessEndInteraction(", StringComparison.Ordinal)) return null;
        var m = EndRx().Match(line);
        if (!m.Success) return null;

        if (!long.TryParse(m.Groups["id"].Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var id))
            return null;
        return new InteractionEndEvent(timestamp, id);
    }
}
