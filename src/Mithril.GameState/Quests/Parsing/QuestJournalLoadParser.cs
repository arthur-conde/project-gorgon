using System.Globalization;
using System.Text.RegularExpressions;
using Mithril.Shared.Logging;

namespace Mithril.GameState.Quests.Parsing;

/// <summary>
/// Parses the bulk <c>ProcessLoadQuests</c> (plural) login signal — a single
/// log line carrying the player's full quest journal as two int lists. Lists
/// use trailing-comma format <c>[a,b,c,]</c>; either list may be empty.
///
/// The captured shape (one of two from a 2026-04-30 capture, ~3KB):
/// <code>
/// LocalPlayer: ProcessLoadQuests(8285856, TransitionalQuestState[],
///   [50208,51252,...,50675,], [3,4,5,...,21501,])
/// </code>
///
/// Wiki: https://github.com/moumantai-gg/mithril/wiki/Player-Log-Signals#processloadquests--bulk-on-login
/// </summary>
public sealed partial class QuestJournalLoadParser : ILogParser
{
    [GeneratedRegex(
        @"LocalPlayer:\s*ProcessLoadQuests\(\s*-?\d+\s*,\s*TransitionalQuestState\[\]\s*,\s*\[(?<a>[\d, ]*)\]\s*,\s*\[(?<b>[\d, ]*)\]\s*\)",
        RegexOptions.CultureInvariant)]
    private static partial Regex LoadRx();

    public LogEvent? TryParse(string line, DateTime timestamp)
    {
        if (!line.Contains("ProcessLoadQuests(", StringComparison.Ordinal)) return null;
        var m = LoadRx().Match(line);
        if (!m.Success) return null;

        var workOrders = ParseIdList(m.Groups["a"].Value);
        var regulars = ParseIdList(m.Groups["b"].Value);
        return new QuestJournalLoadedEvent(timestamp, workOrders, regulars);
    }

    private static IReadOnlyList<int> ParseIdList(string body)
    {
        if (string.IsNullOrWhiteSpace(body)) return Array.Empty<int>();
        var parts = body.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var ids = new List<int>(parts.Length);
        foreach (var p in parts)
        {
            if (int.TryParse(p, NumberStyles.Integer, CultureInfo.InvariantCulture, out var id))
                ids.Add(id);
        }
        return ids;
    }
}
