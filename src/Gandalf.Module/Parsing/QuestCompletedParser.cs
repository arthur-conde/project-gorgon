using System.Text.RegularExpressions;
using Mithril.Shared.Logging;

namespace Gandalf.Parsing;

/// <summary>
/// Parses <c>ProcessCompleteQuest</c> lines emitted when a repeatable quest is
/// turned in. Anchors the cooldown clock on this Timestamp so log-replay
/// produces the right elapsed time.
///
/// Captured shape (wiki Player-Log-Signals § Quest signals): two integer
/// args — <c>ProcessCompleteQuest(&lt;charEntityId&gt;, &lt;questId&gt;)</c> — not
/// the quoted-string form this regex matches. Real-shape rewrite tracked in
/// #77; until that lands the parser silently no-ops on every real line.
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
