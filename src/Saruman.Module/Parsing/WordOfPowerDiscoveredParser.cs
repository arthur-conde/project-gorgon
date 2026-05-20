using System.Text.RegularExpressions;
using Mithril.Shared.Logging;
using Saruman.Domain;

namespace Saruman.Parsing;

/// <summary>
/// Parses Project Gorgon's "you discovered a word of power" <c>ProcessBook</c>
/// line into a typed <see cref="WordOfPowerDiscovered"/> event.
///
/// <para>Post-#550 L1 migration: consumes the envelope-stripped
/// <see cref="LocalPlayerLogLine.Data"/> payload — L0.5 (#532) has already
/// classified the line as <c>LocalPlayer:</c>-actored and eaten the envelope,
/// so the regex no longer re-anchors on it. A cheap substring pre-check
/// short-circuits the (overwhelmingly common) unrelated line before any
/// regex runs.</para>
/// </summary>
public sealed partial class WordOfPowerDiscoveredParser : ILogParser
{
    // The second ProcessBook argument contains the discovery narrative. Newlines
    // inside the string are stored as literal two-char \n escapes (backslash + n),
    // not real newlines, so `\\n` in the pattern matches the file bytes.
    //
    // Post-L1: no `LocalPlayer:` anchor — the envelope is eaten upstream by
    // L0.5 and never re-matched downstream (#550 PR #555 review).
    [GeneratedRegex(
        """ProcessBook\("You discovered a word of power!",\s*"You've discovered a word of power: <sel>(?<code>[A-Z]+)</sel>.*?<b><size=125%>Word of Power: (?<effect>[^<]+)</size></b>\\n(?<desc>.+?)\\n\\n<i>""",
        RegexOptions.CultureInvariant)]
    private static partial Regex DiscoveryRx();

    public LogEvent? TryParse(string line, DateTime timestamp)
    {
        if (!line.Contains("You discovered a word of power!", StringComparison.Ordinal))
            return null;

        var m = DiscoveryRx().Match(line);
        if (!m.Success) return null;

        return new WordOfPowerDiscovered(
            timestamp,
            m.Groups["code"].Value,
            m.Groups["effect"].Value.Trim(),
            m.Groups["desc"].Value.Trim());
    }
}
