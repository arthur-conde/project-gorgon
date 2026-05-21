using System.Globalization;
using System.Text.RegularExpressions;
using Mithril.Shared.Diagnostics;
using Mithril.Shared.Logging;

namespace Mithril.GameState.Skills.Parsing;

/// <summary>
/// Parses Project Gorgon's two skill-progression log lines into typed events:
///
/// <list type="bullet">
///   <item><c>ProcessLoadSkills({type=…}, {type=…}, …)</c> — the full
///   skill-table dump emitted at login and on zone / session transitions.
///   Yields a <see cref="SkillsSnapshotEvent"/> carrying every parsed row.</item>
///   <item><c>ProcessUpdateSkill({type=…}, &lt;bool&gt;, &lt;delta&gt;, 0, 0)</c>
///   — a single skill's progression delta. Yields a
///   <see cref="SkillProgressUpdateEvent"/> for the one row in the leading
///   struct; the trailing positionals are deliberately ignored (see
///   <see cref="SkillProgressUpdateEvent"/>).</item>
/// </list>
///
/// Both lines share the identical <c>{type=…,raw=…,bonus=…,xp=…,tnl=…,max=…}</c>
/// struct grammar; <see cref="SkillTupleRx"/> is the single workhorse applied
/// over the whole line (one match for an update, ~125 for a snapshot).
///
/// <para>Post-#550 L1 migration: consumes the envelope-stripped
/// <see cref="LocalPlayerLogLine.Data"/> payload — L0.5 (#532) has already
/// classified the line as <c>LocalPlayer:</c>-actored and eaten the envelope,
/// so the verb guards no longer re-anchor on it. A cheap substring pre-check
/// short-circuits the (overwhelmingly common) unrelated line before any regex
/// runs.</para>
///
/// <para>Samwise consumes the Gardening update via
/// <see cref="IPlayerSkillState.SubscribeChanges"/> (#581 — was a Samwise-side
/// <c>ProcessUpdateSkill.*type=Gardening</c> regex pre-migration). This parser
/// owns the single canonical match; the Samwise consumer filters the
/// channel on <c>SkillKey == "Gardening"</c>.</para>
///
/// <para>Catalogued in <c>log-patterns.json</c> as
/// <c>shared.SkillLogParser.{LoadSkillsRx,UpdateSkillRx,SkillTupleRx}</c>
/// (the <c>shared</c> module prefix matches the precedent set by the other
/// <c>Mithril.GameState</c> parsers in the catalog).</para>
/// </summary>
public sealed partial class SkillLogParser : ILogParser
{
    // Guard: a real ProcessLoadSkills line (full-table dump). Post-L1 the
    // LocalPlayer: actor envelope is already eaten upstream by L0.5; this
    // parser sees only verbs from LocalPlayer-actored lines.
    [GeneratedRegex(@"ProcessLoadSkills\(", RegexOptions.CultureInvariant)]
    private static partial Regex LoadSkillsRx();

    // Guard: a real ProcessUpdateSkill line. The trailing `\{` ensures the
    // leading argument is the skill struct (not some other overload form).
    [GeneratedRegex(@"ProcessUpdateSkill\(\{", RegexOptions.CultureInvariant)]
    private static partial Regex UpdateSkillRx();

    // ProcessUpdateSkill's third positional: XP earned this tick. Anchored on
    // the verb + struct close so it can only match the genuine emitter. The
    // value is chat-corroborated within a level (see SkillProgressUpdateEvent).
    [GeneratedRegex(
        @"ProcessUpdateSkill\(\{[^}]*\},\s*\w+,\s*(?<gained>\d+)",
        RegexOptions.CultureInvariant)]
    private static partial Regex XpGainRx();

    // Workhorse: one skill struct. Applied via Matches() over the whole line —
    // exactly one for an update, the entire table for a snapshot. Skill keys
    // carry underscores (Anatomy_Bears, Performance_Dance) so \w+ is correct;
    // all numeric fields are non-negative integers in observed data.
    [GeneratedRegex(
        @"\{type=(?<type>\w+),raw=(?<raw>\d+),bonus=(?<bonus>\d+),xp=(?<xp>\d+),tnl=(?<tnl>\d+),max=(?<max>\d+)\}",
        RegexOptions.CultureInvariant)]
    private static partial Regex SkillTupleRx();

    private readonly ThrottledWarn _warn;

    /// <summary>
    /// Constructs the parser. Pass an <see cref="IDiagnosticsSink"/> to surface
    /// the rare malformed-row breadcrumb that <see cref="TryToRecord"/> emits
    /// when a numeric token fails <c>(int|long).TryParse</c> (oversized
    /// raw/bonus/xp/tnl/max via grammar drift — guards against issue #525). A
    /// <c>null</c> sink makes the warn a no-op; the throttle window suppresses
    /// a per-line flood from a pathologically corrupt payload.
    /// </summary>
    public SkillLogParser(IDiagnosticsSink? diag = null)
    {
        _warn = new ThrottledWarn(diag, "GameState.Skills");
    }

    public LogEvent? TryParse(string line, DateTime timestamp)
    {
        // Fast path: the vast majority of Player.log lines are neither verb.
        // Substring check before any regex, mirroring the other parsers.
        if (!line.Contains("ProcessLoadSkills", StringComparison.Ordinal)
            && !line.Contains("ProcessUpdateSkill", StringComparison.Ordinal))
        {
            return null;
        }

        if (LoadSkillsRx().IsMatch(line))
        {
            var skills = new List<SkillProgressRecord>();
            foreach (Match m in SkillTupleRx().Matches(line))
            {
                // Per-row row-skip on numeric overflow / non-integer: keep the
                // surrounding ~124 well-formed skills rather than dropping the
                // whole snapshot. The missing row recovers on the next
                // ProcessLoadSkills (zone / session transition). See #525.
                if (TryToRecord(m, out var record))
                    skills.Add(record);
            }

            // A ProcessLoadSkills line with no parseable struct is degenerate
            // (truncated log, grammar drift). Emit nothing rather than a
            // spurious empty snapshot that would wipe live state.
            return skills.Count == 0 ? null : new SkillsSnapshotEvent(timestamp, skills);
        }

        if (UpdateSkillRx().IsMatch(line))
        {
            var m = SkillTupleRx().Match(line);
            if (!m.Success) return null;
            if (!TryToRecord(m, out var record)) return null;

            // arg3 = XP gained this tick. Defaults to 0 if the tail is absent
            // (grammar drift) — the struct is still authoritative for state.
            // An oversized arg3 (would-be overflow) is also treated as 0 with
            // a breadcrumb rather than killing the otherwise-valid update.
            long gained = 0;
            var g = XpGainRx().Match(line);
            if (g.Success
                && !long.TryParse(g.Groups["gained"].ValueSpan, NumberStyles.Integer, CultureInfo.InvariantCulture, out gained))
            {
                _warn.Warn($"ProcessUpdateSkill: unparseable XpGained '{g.Groups["gained"].Value}', defaulting to 0");
                gained = 0;
            }
            return new SkillProgressUpdateEvent(timestamp, record, gained);
        }

        return null;
    }

    private bool TryToRecord(Match m, out SkillProgressRecord record)
    {
        if (int.TryParse(m.Groups["raw"].ValueSpan, NumberStyles.Integer, CultureInfo.InvariantCulture, out var raw)
            && int.TryParse(m.Groups["bonus"].ValueSpan, NumberStyles.Integer, CultureInfo.InvariantCulture, out var bonus)
            && long.TryParse(m.Groups["xp"].ValueSpan, NumberStyles.Integer, CultureInfo.InvariantCulture, out var xp)
            && long.TryParse(m.Groups["tnl"].ValueSpan, NumberStyles.Integer, CultureInfo.InvariantCulture, out var tnl)
            && int.TryParse(m.Groups["max"].ValueSpan, NumberStyles.Integer, CultureInfo.InvariantCulture, out var max))
        {
            record = new SkillProgressRecord(
                SkillKey: m.Groups["type"].Value,
                Level: raw,
                BonusLevels: bonus,
                XpTowardNextLevel: xp,
                XpNeededForNextLevel: tnl,
                MaxLevel: max);
            return true;
        }
        _warn.Warn(
            $"Dropping skill row '{m.Groups["type"].Value}' with unparseable numeric field " +
            $"(raw='{m.Groups["raw"].Value}', bonus='{m.Groups["bonus"].Value}', " +
            $"xp='{m.Groups["xp"].Value}', tnl='{m.Groups["tnl"].Value}', " +
            $"max='{m.Groups["max"].Value}')");
        record = default!;
        return false;
    }
}
