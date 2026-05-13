using System.Globalization;
using System.Text.RegularExpressions;
using Mithril.Shared.Diagnostics;
using Mithril.Shared.Logging;
using Mithril.Shared.Reference;

namespace Mithril.GameState.Quests.Parsing;

/// <summary>
/// Parses <c>ProcessCompleteQuest(&lt;charEntityId&gt;, &lt;questId&gt;)</c> lines emitted
/// when a repeatable quest is turned in. Resolves the integer quest id to the
/// quest's InternalName via <see cref="IReferenceDataService.Quests"/> (keyed
/// by <c>"quest_&lt;id&gt;"</c>) so downstream consumers don't need a second lookup.
/// Anchors the cooldown clock on this Timestamp so log-replay produces the
/// right elapsed time.
///
/// Wiki: https://github.com/moumantai-gg/mithril/wiki/Player-Log-Signals#processcompletequest--quest-turned-in
/// </summary>
public sealed partial class QuestCompletedParser : ILogParser
{
    [GeneratedRegex(
        @"LocalPlayer:\s*ProcessCompleteQuest\(\s*-?\d+\s*,\s*(?<id>\d+)\s*\)",
        RegexOptions.CultureInvariant)]
    private static partial Regex CompleteRx();

    private readonly IReferenceDataService _refData;
    private readonly IDiagnosticsSink? _diag;

    public QuestCompletedParser(IReferenceDataService refData, IDiagnosticsSink? diag = null)
    {
        _refData = refData;
        _diag = diag;
    }

    public LogEvent? TryParse(string line, DateTime timestamp)
    {
        if (!line.Contains("ProcessCompleteQuest(", StringComparison.Ordinal)) return null;
        var m = CompleteRx().Match(line);
        if (!m.Success) return null;

        var idStr = m.Groups["id"].Value;
        if (!int.TryParse(idStr, NumberStyles.Integer, CultureInfo.InvariantCulture, out var id)) return null;

        if (!_refData.Quests.TryGetValue($"quest_{id}", out var entry))
        {
            // Game-data drift / unknown quest — drop silently. Possible if quests.json
            // is out of date relative to the live client (a new quest landed in a
            // patch ahead of the CDN refresh cadence).
            _diag?.Trace("Gandalf.Quest", $"Unknown questId {id}; dropping ProcessCompleteQuest line");
            return null;
        }

        return new QuestCompletedEvent(timestamp, entry.InternalName);
    }
}
