using System.Text.RegularExpressions;
using Mithril.Shared.Logging;

namespace Gandalf.Parsing;

/// <summary>
/// Parses <c>ProcessStartInteraction</c> lines for static-chest interactions.
/// Per the wiki page Player-Log-Signals (Static treasure chests):
/// <c>LocalPlayer: ProcessStartInteraction(-162, 7, 0, False, "GoblinStaticChest1")</c>
/// where the trailing quoted token is the chest's prefab name. The integer
/// interactor id is reused across static interactors, so we key off the prefab
/// name only.
///
/// v1 emits an event for *every* StartInteraction whose entity name ends with
/// "StaticChest" (the convention observed in samples). Refines later if other
/// chest naming patterns surface — the catalog cache absorbs the discovery
/// either way.
/// </summary>
public sealed partial class ChestInteractionParser : ILogParser
{
    [GeneratedRegex(
        """LocalPlayer:\s*ProcessStartInteraction\(-?\d+,\s*\d+,\s*\d+,\s*(?:True|False),\s*"(?<name>[^"]+)"\)""",
        RegexOptions.CultureInvariant)]
    private static partial Regex InteractionRx();

    public LogEvent? TryParse(string line, DateTime timestamp)
    {
        if (!line.Contains("ProcessStartInteraction(", StringComparison.Ordinal)) return null;
        var m = InteractionRx().Match(line);
        if (!m.Success) return null;

        var name = m.Groups["name"].Value;
        // Static chests follow the "<Theme>StaticChest<N>" convention in observed
        // samples (GoblinStaticChest1). Filter to that prefix shape so we don't
        // create timers for vendor interactions, NPC dialog, etc.
        if (!name.Contains("StaticChest", StringComparison.Ordinal)) return null;

        return new ChestInteractionEvent(timestamp, name);
    }
}
