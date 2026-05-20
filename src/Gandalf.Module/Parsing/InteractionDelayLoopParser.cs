using System.Text.RegularExpressions;
using Mithril.Shared.Logging;

namespace Gandalf.Parsing;

/// <summary>
/// Parses <c>LocalPlayer: ProcessDoDelayLoop(...)</c> into an
/// <see cref="InteractionDelayLoopEvent"/>. The bracket tracker uses the
/// <c>IsInteractorDelayLoop</c> trailing flag to discriminate harvest-style
/// interactions (Gather "Collecting Fruit..." on a LemonTree) from chests:
/// when set, the in-flight bracket is a harvest and the subsequent
/// <c>ProcessAddItem</c> must not commit a chest row.
///
/// Sample shapes from live capture (#91):
/// <c>ProcessDoDelayLoop(3, Gather, "Collecting Fruit...", 0, AbortIfAttacked, IsInteractorDelayLoop)</c> — harvest
/// <c>ProcessDoDelayLoop(1.5, Eat, "Using Ranalon Salad", 5820, AbortIfAttacked)</c> — self-targeted, no flag
/// <c>ProcessDoDelayLoop(10, UseTeleportationCircle, "Recalling Other Places", 3713, AbortIfAttacked)</c> — self-targeted, no flag
/// </summary>
public sealed partial class InteractionDelayLoopParser : ILogParser
{
    // L0.5 (#532) eats the [ts] + LocalPlayer: envelope; downstream never
    // re-matches the actor envelope (#550 PR #555 review). The L1 driver
    // hands LocalPlayerLogLine.Data verbatim, so this regex sees just the
    // ProcessDoDelayLoop(...) body.
    [GeneratedRegex(
        """ProcessDoDelayLoop\(\s*\d+(?:\.\d+)?\s*,\s*(?<verb>\w+)\s*,\s*"[^"]*"\s*,\s*-?\d+\s*,\s*\w+(?<flags>(?:\s*,\s*\w+)*)\s*\)""",
        RegexOptions.CultureInvariant)]
    private static partial Regex DelayLoopRx();

    public LogEvent? TryParse(string line, DateTime timestamp)
    {
        if (!line.Contains("ProcessDoDelayLoop(", StringComparison.Ordinal)) return null;
        var m = DelayLoopRx().Match(line);
        if (!m.Success) return null;

        var verb = m.Groups["verb"].Value;
        var isInteractor = m.Groups["flags"].Value.Contains("IsInteractorDelayLoop", StringComparison.Ordinal);
        return new InteractionDelayLoopEvent(timestamp, verb, isInteractor);
    }
}
