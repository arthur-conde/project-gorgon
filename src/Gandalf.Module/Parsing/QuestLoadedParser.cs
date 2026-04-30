using System.Text.RegularExpressions;
using Mithril.Shared.Logging;

namespace Gandalf.Parsing;

/// <summary>
/// Parses <c>ProcessLoadQuest</c> lines emitted when a quest enters the
/// player's journal (login replay or fresh acceptance).
///
/// **Verification owed.** The exact line shape was deferred from the parser
/// spike (#60). The regex below is a plausible match against the
/// <c>LocalPlayer: ProcessXxx(...)</c> family — first quoted argument is the
/// quest's InternalName. Refines once captured samples land in the wiki.
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
