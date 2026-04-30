using System.Text.RegularExpressions;
using Mithril.Shared.Logging;

namespace Gandalf.Parsing;

/// <summary>
/// Parses <c>ProcessCompleteQuest</c> lines emitted when a repeatable quest is
/// turned in. Anchors the cooldown clock on this Timestamp so log-replay
/// produces the right elapsed time.
///
/// **Verification owed.** Same caveat as <see cref="QuestLoadedParser"/> —
/// regex is a plausible <c>LocalPlayer: ProcessXxx("InternalName"...)</c>
/// match pending real captures. Refines once samples land.
/// </summary>
public sealed partial class QuestCompletedParser : ILogParser
{
    [GeneratedRegex(
        "LocalPlayer:\\s*ProcessCompleteQuest\\(\"(?<name>[^\"]+)\"",
        RegexOptions.CultureInvariant)]
    private static partial Regex CompleteRx();

    public LogEvent? TryParse(string line, DateTime timestamp)
    {
        if (!line.Contains("ProcessCompleteQuest(", StringComparison.Ordinal)) return null;
        var m = CompleteRx().Match(line);
        if (!m.Success) return null;

        var name = m.Groups["name"].Value;
        return string.IsNullOrEmpty(name) ? null : new QuestCompletedEvent(timestamp, name);
    }
}
