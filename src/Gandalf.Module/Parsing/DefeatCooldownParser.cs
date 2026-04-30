using System.Text.RegularExpressions;
using Mithril.Shared.Logging;

namespace Gandalf.Parsing;

/// <summary>
/// Parses the two signals emitted by reward-cooldown creatures (Megaspider,
/// Olugax The Ever-Pudding, and similar). The 2026-04-30 capture of Olugax
/// emitting the same <c>"too recently"</c> rejection text confirmed both
/// classes share one signal mechanism, so a single parser handles them.
///
/// <list type="bullet">
///   <item><b>Positive (corpse search):</b>
///     <c>ProcessTalkScreen(&lt;id&gt;, "Search Corpse of &lt;Name&gt;", …, Corpse)</c>
///     fires for every corpse a player can search. Every kill emits one
///     (boss or trash mob) so the parser stays permissive — <c>LootSource</c>
///     filters down to entries in the defeat catalog.</item>
///   <item><b>Negative (rejection):</b>
///     <c>ProcessScreenText(GeneralInfo, "You have already killed &lt;Name&gt; too recently. …")</c>
///     fires when the player tries to engage a still-cooling boss. v1 surfaces
///     this as a diagnostic-only event — the cooldown row was anchored by the
///     prior kill's positive path.</item>
/// </list>
///
/// Wiki: https://github.com/arthur-conde/project-gorgon/wiki/Player-Log-Signals#defeat-cooldown-creatures
/// </summary>
public sealed partial class DefeatCooldownParser : ILogParser
{
    [GeneratedRegex(
        @"LocalPlayer:\s*ProcessTalkScreen\(\s*-?\d+\s*,\s*""Search Corpse of (?<npc>[^""]+)""",
        RegexOptions.CultureInvariant)]
    private static partial Regex CorpseRx();

    [GeneratedRegex(
        @"LocalPlayer:\s*ProcessScreenText\(GeneralInfo,\s*""You have already killed (?<npc>.+?) too recently\.",
        RegexOptions.CultureInvariant)]
    private static partial Regex RejectionRx();

    public LogEvent? TryParse(string line, DateTime timestamp)
    {
        // Positive path — fires for every corpse-search, including non-boss mobs.
        // Cheap substring gate keeps the regex hit-rate sane on combat-heavy logs.
        if (line.Contains("\"Search Corpse of ", StringComparison.Ordinal))
        {
            var m = CorpseRx().Match(line);
            if (m.Success)
            {
                var npc = m.Groups["npc"].Value.Trim();
                if (!string.IsNullOrEmpty(npc))
                    return new DefeatCooldownObservedEvent(timestamp, npc);
            }
        }

        if (line.Contains("too recently", StringComparison.Ordinal))
        {
            var m = RejectionRx().Match(line);
            if (m.Success)
            {
                var npc = m.Groups["npc"].Value.Trim();
                if (!string.IsNullOrEmpty(npc))
                    return new DefeatCooldownActiveEvent(timestamp, npc);
            }
        }

        return null;
    }
}

/// <summary>
/// Player just searched a corpse. Permissive event — fires for every mob, not
/// just bosses. <see cref="Services.LootSource"/> filters down to entries in
/// the defeat catalog.
/// </summary>
public sealed record DefeatCooldownObservedEvent(DateTime Timestamp, string NpcDisplayName)
    : LogEvent(Timestamp);

/// <summary>
/// Player tried to engage a boss whose reward cooldown is still running. v1 is
/// diagnostic-only — confirms the cooldown clock is alive but does not mutate
/// progress (the prior kill's positive signal already anchored the row).
/// </summary>
public sealed record DefeatCooldownActiveEvent(DateTime Timestamp, string NpcDisplayName)
    : LogEvent(Timestamp);
