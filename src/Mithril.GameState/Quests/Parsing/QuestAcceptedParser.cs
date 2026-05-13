using System.Globalization;
using System.Text.RegularExpressions;
using Mithril.Shared.Diagnostics;
using Mithril.Shared.Logging;
using Mithril.Shared.Reference;

namespace Mithril.GameState.Quests.Parsing;

/// <summary>
/// Parses the per-accept <c>ProcessBook("New Quest: &lt;&lt;&lt;quest_NNNNN_Name&gt;&gt;&gt;", ...)</c>
/// signal that fires alongside the opaque <c>ProcessAddQuest</c> call. The
/// <c>"New Quest: "</c> prefix is unique to quest acceptance — no pairing with
/// <c>ProcessAddQuest</c> is needed.
///
/// Resolves the integer quest id to the quest's InternalName via
/// <see cref="IReferenceDataService.Quests"/> so downstream consumers
/// (e.g. <c>QuestService</c>) can track the journal by stable name.
///
/// Wiki: https://github.com/moumantai-gg/mithril/wiki/Player-Log-Signals#processaddquest--quest-accepted
/// </summary>
public sealed partial class QuestAcceptedParser : ILogParser
{
    [GeneratedRegex(
        @"LocalPlayer:\s*ProcessBook\(""New Quest: <<<quest_(?<id>\d+)_Name>>>""",
        RegexOptions.CultureInvariant)]
    private static partial Regex AcceptRx();

    private readonly IReferenceDataService _refData;
    private readonly IDiagnosticsSink? _diag;

    public QuestAcceptedParser(IReferenceDataService refData, IDiagnosticsSink? diag = null)
    {
        _refData = refData;
        _diag = diag;
    }

    public LogEvent? TryParse(string line, DateTime timestamp)
    {
        if (!line.Contains("ProcessBook(\"New Quest:", StringComparison.Ordinal)) return null;
        var m = AcceptRx().Match(line);
        if (!m.Success) return null;

        if (!int.TryParse(m.Groups["id"].Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var id))
            return null;

        if (!_refData.Quests.TryGetValue($"quest_{id}", out var entry))
        {
            _diag?.Trace("Gandalf.Quest", $"Unknown questId {id}; dropping ProcessBook \"New Quest\" line");
            return null;
        }

        return new QuestAcceptedEvent(timestamp, entry.InternalName);
    }
}
