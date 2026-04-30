using System.Text.RegularExpressions;
using Mithril.Shared.Logging;

namespace Gandalf.Parsing;

/// <summary>
/// Parses the kill-credit screen text per the wiki page Player-Log-Signals
/// (Scripted-event bosses). Sample line:
/// <c>LocalPlayer: ProcessScreenText(CombatInfo, "You earned 12 Combat Wisdom: Killed Olugax the Ever-Pudding")</c>
///
/// Correct for the **scripted-event class** (Olugax) — kill-credit fires on
/// every kill regardless of cooldown; <c>LootSource</c>'s in-flight check
/// suppresses duplicates within the cooldown window. The "reduced rewards"
/// signal that would distinguish rewarded from cooldown-suppressed kills in
/// this class is still verification-owed.
///
/// Wrong for the **defeat-cooldown class** (Megaspider) — the spike (#60)
/// confirmed the game suppresses the kill-credit line entirely on a
/// within-cooldown re-kill, and emits a parseable rejection text instead. A
/// dedicated <c>DefeatCooldownParser</c> for that class is tracked in #79.
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
