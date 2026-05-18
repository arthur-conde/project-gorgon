using System.Text.RegularExpressions;
using Mithril.Shared.Logging;

namespace Mithril.GameState.Skills.Parsing;

/// <summary>
/// Parses Project Gorgon's two skill-progression log lines into typed events:
///
/// <list type="bullet">
///   <item><c>LocalPlayer: ProcessLoadSkills({type=…}, {type=…}, …)</c> — the
///   full skill-table dump emitted at login and on zone / session transitions.
///   Yields a <see cref="SkillsSnapshotEvent"/> carrying every parsed row.</item>
///   <item><c>LocalPlayer: ProcessUpdateSkill({type=…}, &lt;bool&gt;, &lt;delta&gt;, 0, 0)</c>
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
/// <para>The regexes are unanchored — the same convention every other
/// <c>Player.log</c> parser in the codebase uses — and the
/// <see cref="LoadSkillsRx"/> / <see cref="UpdateSkillRx"/> guards both pin the
/// <c>LocalPlayer:</c> prefix so an unrelated line that merely mentions the
/// verb cannot trigger a parse. A cheap substring pre-check short-circuits the
/// (overwhelmingly common) unrelated line before any regex runs.</para>
///
/// <para>This parser is independent of Samwise's
/// <c>GardenLogParser.GardeningXpRx</c> (<c>ProcessUpdateSkill.*type=Gardening</c>):
/// both can match the same Gardening update line; Samwise consumes it as a
/// gardening heartbeat while this parser folds Gardening into the full skill
/// state. There is no coupling or ordering dependency between them.</para>
///
/// <para>Catalogued in <c>log-patterns.json</c> as
/// <c>shared.SkillLogParser.{LoadSkillsRx,UpdateSkillRx,SkillTupleRx}</c>
/// (the <c>shared</c> module prefix matches the precedent set by the other
/// <c>Mithril.GameState</c> parsers in the catalog).</para>
/// </summary>
public sealed partial class SkillLogParser : ILogParser
{
    // Guard: a real ProcessLoadSkills line (full-table dump). Pins the
    // LocalPlayer: prefix so only the genuine emitter matches.
    [GeneratedRegex(@"LocalPlayer: ProcessLoadSkills\(", RegexOptions.CultureInvariant)]
    private static partial Regex LoadSkillsRx();

    // Guard: a real ProcessUpdateSkill line. The trailing `\{` ensures the
    // leading argument is the skill struct (not some other overload form).
    [GeneratedRegex(@"LocalPlayer: ProcessUpdateSkill\(\{", RegexOptions.CultureInvariant)]
    private static partial Regex UpdateSkillRx();

    // Workhorse: one skill struct. Applied via Matches() over the whole line —
    // exactly one for an update, the entire table for a snapshot. Skill keys
    // carry underscores (Anatomy_Bears, Performance_Dance) so \w+ is correct;
    // all numeric fields are non-negative integers in observed data.
    [GeneratedRegex(
        @"\{type=(?<type>\w+),raw=(?<raw>\d+),bonus=(?<bonus>\d+),xp=(?<xp>\d+),tnl=(?<tnl>\d+),max=(?<max>\d+)\}",
        RegexOptions.CultureInvariant)]
    private static partial Regex SkillTupleRx();

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
                skills.Add(ToRecord(m));
            }

            // A ProcessLoadSkills line with no parseable struct is degenerate
            // (truncated log, grammar drift). Emit nothing rather than a
            // spurious empty snapshot that would wipe live state.
            return skills.Count == 0 ? null : new SkillsSnapshotEvent(timestamp, skills);
        }

        if (UpdateSkillRx().IsMatch(line))
        {
            var m = SkillTupleRx().Match(line);
            return m.Success ? new SkillProgressUpdateEvent(timestamp, ToRecord(m)) : null;
        }

        return null;
    }

    private static SkillProgressRecord ToRecord(Match m) => new(
        SkillKey: m.Groups["type"].Value,
        Level: int.Parse(m.Groups["raw"].ValueSpan),
        BonusLevels: int.Parse(m.Groups["bonus"].ValueSpan),
        XpTowardNextLevel: long.Parse(m.Groups["xp"].ValueSpan),
        XpNeededForNextLevel: long.Parse(m.Groups["tnl"].ValueSpan),
        MaxLevel: int.Parse(m.Groups["max"].ValueSpan));
}
