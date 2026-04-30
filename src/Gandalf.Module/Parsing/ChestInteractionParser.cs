using System.Globalization;
using System.Text.RegularExpressions;
using Mithril.Shared.Logging;

namespace Gandalf.Parsing;

/// <summary>
/// Parses any <c>ProcessStartInteraction</c> line into an
/// <see cref="InteractionStartEvent"/>. The parser is intentionally broad —
/// loot vs storage vs NPC discrimination happens downstream in
/// <c>LootBracketTracker</c> based on which other signals fire inside the
/// bracket (<c>ProcessAddItem</c> = loot, <c>ProcessTalkScreen</c> = UI dialog).
///
/// #64 v1 used a <c>Contains("StaticChest")</c> name filter, which silently
/// dropped real loot prefabs like <c>EltibuleSecretChest</c>; live-log
/// verification under #73 moved the filter from naming to signal.
///
/// Wiki sample:
/// <c>LocalPlayer: ProcessStartInteraction(-162, 7, 0, False, "GoblinStaticChest1")</c>
/// </summary>
public sealed partial class ChestInteractionParser : ILogParser
{
    [GeneratedRegex(
        """LocalPlayer:\s*ProcessStartInteraction\((?<id>-?\d+),\s*\d+,\s*\d+,\s*(?:True|False),\s*"(?<name>[^"]+)"\)""",
        RegexOptions.CultureInvariant)]
    private static partial Regex InteractionRx();

    public LogEvent? TryParse(string line, DateTime timestamp)
    {
        if (!line.Contains("ProcessStartInteraction(", StringComparison.Ordinal)) return null;
        var m = InteractionRx().Match(line);
        if (!m.Success) return null;

        if (!long.TryParse(m.Groups["id"].Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var id))
            return null;
        return new InteractionStartEvent(timestamp, id, m.Groups["name"].Value);
    }
}
