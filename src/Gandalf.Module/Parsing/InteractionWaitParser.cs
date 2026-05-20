using System.Globalization;
using System.Text.RegularExpressions;
using Mithril.Shared.Logging;

namespace Gandalf.Parsing;

/// <summary>
/// Parses <c>LocalPlayer: ProcessWaitInteraction(id, ms, "verb", "body")</c>.
/// Semantically equivalent to a <see cref="InteractionDelayLoopEvent"/> with
/// <c>IsInteractorDelayLoop</c> set, but emitted via a different signal —
/// the existing <see cref="InteractionDelayLoopParser"/> never sees it.
///
/// Live captures (#174):
/// <c>ProcessWaitInteraction(-2, 500, "Filling Water Bottles...", "...")</c> — WaterWell harvest
/// <c>ProcessWaitInteraction(-45, 500, "", "")</c> — IvynsChest unlock animation (empty body)
/// </summary>
public sealed partial class InteractionWaitParser : ILogParser
{
    // L0.5 (#532) eats the [ts] + LocalPlayer: envelope; downstream never
    // re-matches the actor envelope (#550 PR #555 review). The L1 driver
    // hands LocalPlayerLogLine.Data verbatim, so this regex sees just the
    // ProcessWaitInteraction(...) body.
    [GeneratedRegex(
        """ProcessWaitInteraction\((?<id>-?\d+),\s*\d+,\s*"(?<body>[^"]*)",""",
        RegexOptions.CultureInvariant)]
    private static partial Regex WaitRx();

    public LogEvent? TryParse(string line, DateTime timestamp)
    {
        if (!line.Contains("ProcessWaitInteraction(", StringComparison.Ordinal)) return null;
        var m = WaitRx().Match(line);
        if (!m.Success) return null;

        if (!long.TryParse(m.Groups["id"].Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var id))
            return null;
        return new InteractionWaitEvent(timestamp, id, m.Groups["body"].Value);
    }
}
