using System.Text.RegularExpressions;
using Mithril.Shared.Logging;

namespace Gandalf.Parsing;

/// <summary>
/// Parses the kill-credit screen text per the wiki page Player-Log-Signals
/// (Scripted-event bosses). Sample line:
/// <c>LocalPlayer: ProcessScreenText(CombatInfo, "You earned 12 Combat Wisdom: Killed Olugax the Ever-Pudding")</c>
///
/// **Verification owed.** Per the wiki, the kill-credit line fires on every kill
/// regardless of cooldown state — both rewarded and cooldown-suppressed kills
/// produce identical lines. The "reduced rewards" log line that distinguishes
/// the two has not been pinned down. Until it is, v1 anchors the cooldown on
/// the kill itself and lets <c>LootSource</c>'s in-flight check suppress
/// duplicate triggers within the cooldown window.
/// </summary>
public sealed partial class DefeatRewardParser : ILogParser
{
    [GeneratedRegex(
        """LocalPlayer:\s*ProcessScreenText\(CombatInfo,\s*"You earned [\d\.]+\s*[A-Za-z ]+:\s*Killed (?<npc>[^"]+)"\)""",
        RegexOptions.CultureInvariant)]
    private static partial Regex KillCreditRx();

    public LogEvent? TryParse(string line, DateTime timestamp)
    {
        if (!line.Contains(": Killed ", StringComparison.Ordinal)) return null;
        var m = KillCreditRx().Match(line);
        if (!m.Success) return null;

        var npc = m.Groups["npc"].Value.Trim();
        return string.IsNullOrEmpty(npc) ? null : new DefeatRewardEvent(timestamp, npc);
    }
}
