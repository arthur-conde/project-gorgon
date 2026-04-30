using System.Text.RegularExpressions;
using Mithril.Shared.Logging;

namespace Gandalf.Parsing;

/// <summary>
/// Parses the kill-credit screen text emitted for the scripted-event boss
/// class (Olugax The Ever-Pudding and similar). Sample line:
/// <c>LocalPlayer: ProcessScreenText(CombatInfo, "You earned 12 Combat Wisdom: Killed Olugax the Ever-Pudding")</c>
///
/// **Verification owed (class-specific).** For this class the kill-credit line
/// fires on every kill regardless of cooldown — both rewarded and
/// cooldown-suppressed kills produce identical lines. The "reduced rewards"
/// log line that distinguishes the two has not been pinned down. Until it is,
/// v1 anchors the cooldown on the kill itself and lets <c>LootSource</c>'s
/// in-flight check suppress duplicate triggers within the cooldown window.
///
/// Wiki: https://github.com/arthur-conde/project-gorgon/wiki/Player-Log-Signals#scripted-event-bosses
/// </summary>
public sealed partial class ScriptedEventBossParser : ILogParser
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
        return string.IsNullOrEmpty(npc) ? null : new ScriptedEventBossDefeatedEvent(timestamp, npc);
    }
}

/// <summary>
/// Player got kill credit for a scripted-event-class boss. Anchors the local
/// reward-cooldown clock; LootSource's in-flight check debounces within-window
/// re-kills.
/// </summary>
public sealed record ScriptedEventBossDefeatedEvent(DateTime Timestamp, string NpcDisplayName)
    : LogEvent(Timestamp);
