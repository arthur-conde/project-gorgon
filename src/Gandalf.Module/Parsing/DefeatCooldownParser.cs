using System.Text.RegularExpressions;
using Mithril.Shared.Logging;

namespace Gandalf.Parsing;

/// <summary>
/// Parses the "you have already killed &lt;X&gt; too recently" rejection screen
/// text emitted when the player tries to engage a defeat-cooldown boss whose
/// reward cooldown is still running. Diagnostic-only — the prior kill's
/// wisdom-credit line (see <see cref="BossKillCreditParser"/>) already stamped
/// the cooldown row; the rejection just confirms the clock is alive.
///
/// Captured Megaspider + Olugax samples in the wiki — both bosses share this
/// signal; classifying which one emits it is unnecessary because the cooldown
/// row is anchored on the prior kill, not the rejection.
///
/// Wiki: https://github.com/moumantai-gg/mithril/wiki/Player-Log-Signals#defeat-cooldown-creatures
/// </summary>
public sealed partial class DefeatCooldownParser : ILogParser
{
    [GeneratedRegex(
        @"LocalPlayer:\s*ProcessScreenText\(GeneralInfo,\s*""You have already killed (?<npc>.+?) too recently\.",
        RegexOptions.CultureInvariant)]
    private static partial Regex RejectionRx();

    public LogEvent? TryParse(string line, DateTime timestamp)
    {
        if (!line.Contains("too recently", StringComparison.Ordinal)) return null;
        var m = RejectionRx().Match(line);
        if (!m.Success) return null;

        var npc = m.Groups["npc"].Value.Trim();
        return string.IsNullOrEmpty(npc) ? null : new DefeatCooldownActiveEvent(timestamp, npc);
    }
}

/// <summary>
/// Player tried to engage a boss whose reward cooldown is still running.
/// Diagnostic-only — confirms the cooldown clock is alive but does not mutate
/// progress (the prior kill's wisdom-credit signal already anchored the row).
/// </summary>
public sealed record DefeatCooldownActiveEvent(DateTime Timestamp, string NpcDisplayName)
    : LogEvent(Timestamp);
