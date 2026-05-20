using System.Text.RegularExpressions;
using Mithril.Shared.Logging;

namespace Gandalf.Parsing;

/// <summary>
/// Parses the CombatInfo wisdom-credit line emitted on every defeat-cooldown
/// boss kill that successfully cleared the cooldown:
/// <c>LocalPlayer: ProcessScreenText(CombatInfo, "You earned &lt;N&gt; Combat Wisdom: Killed &lt;Name&gt;")</c>.
///
/// Combat Wisdom is awarded only on defeat-cooldown creature kills (per the
/// wiki section "Defeat-cooldown creatures § Cooldown-anchor signals"), so the
/// presence of this line is itself the "this NPC is a boss" signal — no
/// hand-curated catalog needed. The wisdom line is suppressed in lockstep
/// with the loot bracket on a within-cooldown re-kill, so observing it means
/// the cooldown clock just (re)started server-side.
///
/// The kill-credit phrasing has minor variants:
/// <list type="bullet">
///   <item><c>"…: Killed Olugax the Ever-Pudding"</c> — proper-noun, no article</item>
///   <item><c>"…: Killed a Mega-Spider"</c> — common-noun, leading "a"</item>
///   <item><c>"…: Killed the Den Mother"</c> — common-noun, leading "the"</item>
///   <item><c>"…: Killed the Ranalon Doctrine-Keeper"</c> — same shape, multi-word</item>
/// </list>
/// The parser strips a leading <c>a / an / the</c> article so the catalog key
/// stays consistent across the variants. The stripped form is what calibration
/// overlays key on (<c>aggregated/gandalf.json</c>).
///
/// Wiki: https://github.com/moumantai-gg/mithril/wiki/Player-Log-Signals#defeat-cooldown-creatures
/// </summary>
public sealed partial class BossKillCreditParser : ILogParser
{
    // L0.5 (#532) eats the [ts] + LocalPlayer: envelope; downstream never
    // re-matches the actor envelope (#550 PR #555 review). The L1 driver
    // hands LocalPlayerLogLine.Data verbatim, so this regex sees just the
    // ProcessScreenText(...) body. The substring guard above is unchanged
    // — it's the cheap hot-path filter, not an envelope check.
    [GeneratedRegex(
        """ProcessScreenText\(CombatInfo,\s*"You earned [\d\.]+\s*Combat Wisdom:\s*Killed (?:a |an |the )?(?<npc>[^"]+)"\)""",
        RegexOptions.CultureInvariant | RegexOptions.IgnoreCase)]
    private static partial Regex KillCreditRx();

    public LogEvent? TryParse(string line, DateTime timestamp)
    {
        // Cheap substring gate keeps the regex hit-rate sane — combat-heavy logs
        // emit thousands of CombatInfo lines per session.
        if (!line.Contains("Combat Wisdom: Killed", StringComparison.Ordinal)) return null;

        var m = KillCreditRx().Match(line);
        if (!m.Success) return null;

        var npc = m.Groups["npc"].Value.Trim();
        return string.IsNullOrEmpty(npc) ? null : new BossKillCreditEvent(timestamp, npc);
    }
}

/// <summary>
/// Player got kill credit on a defeat-cooldown boss. Anchors the cooldown row
/// AND auto-learns the boss into the persisted catalog — discovery is
/// observation-driven, no per-boss code change.
/// </summary>
public sealed record BossKillCreditEvent(DateTime Timestamp, string NpcDisplayName)
    : LogEvent(Timestamp);
