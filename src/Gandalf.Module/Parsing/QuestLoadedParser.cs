using System.Text.RegularExpressions;
using Mithril.Shared.Logging;

namespace Gandalf.Parsing;

/// <summary>
/// Originally intended to parse a per-quest "loaded" line. The spike (#60)
/// confirmed no such line exists in <c>Player.log</c>: login emits a single
/// bulk <c>ProcessLoadQuests</c> (plural) carrying two ID lists, and per-quest
/// acceptance is signalled via <c>ProcessAddQuest</c> + companion
/// <c>ProcessBook("New Quest: &lt;&lt;&lt;quest_NNNNN_Name&gt;&gt;&gt;", …)</c>.
/// See wiki Player-Log-Signals § Quest signals.
///
/// Redesign (rebuild as bulk-load + accept parsers, or drop) tracked in #78.
/// Until that lands the parser silently no-ops on every real line.
/// </summary>
public sealed partial class QuestLoadedParser : ILogParser
{
    [GeneratedRegex(
        "LocalPlayer:\\s*ProcessLoadQuest\\(\"(?<name>[^\"]+)\"",
        RegexOptions.CultureInvariant)]
    private static partial Regex LoadRx();

    public LogEvent? TryParse(string line, DateTime timestamp)
    {
        if (!line.Contains("ProcessLoadQuest(", StringComparison.Ordinal)) return null;
        var m = LoadRx().Match(line);
        if (!m.Success) return null;

        var name = m.Groups["name"].Value;
        return string.IsNullOrEmpty(name) ? null : new QuestLoadedEvent(timestamp, name);
    }
}
