using System.Text.RegularExpressions;
using Gorgon.Shared.Logging;
using Saruman.Domain;

namespace Saruman.Parsing;

public sealed partial class WordOfPowerDiscoveredParser : ILogParser
{
    // The second ProcessBook argument contains the discovery narrative. Newlines
    // inside the string are stored as literal two-char \n escapes (backslash + n),
    // not real newlines, so `\\n` in the pattern matches the file bytes.
    [GeneratedRegex(
        """LocalPlayer:\s*ProcessBook\("You discovered a word of power!",\s*"You've discovered a word of power: <sel>(?<code>[A-Z]+)</sel>.*?<b><size=125%>Word of Power: (?<effect>[^<]+)</size></b>\\n(?<desc>.+?)\\n\\n<i>""",
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
