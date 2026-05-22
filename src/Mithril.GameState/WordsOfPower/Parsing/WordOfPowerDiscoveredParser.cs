using System.Text.RegularExpressions;

namespace Mithril.GameState.WordsOfPower.Parsing;

/// <summary>
/// Parses Project Gorgon's "you discovered a word of power" <c>ProcessBook</c>
/// line — emitted whenever the player learns a new Word-of-Power code — into
/// its <c>(code, effect-name, description)</c> triple. Migrated to
/// <c>Mithril.GameState</c> per the #603 split: the parser is producer-owned
/// infrastructure, not module-internal.
///
/// <para>Consumes the envelope-stripped
/// <c>LocalPlayerLogLine.Data</c> payload — L0.5 (#532) has already classified
/// the line as <c>LocalPlayer:</c>-actored and eaten the envelope. A cheap
/// substring pre-check short-circuits the (overwhelmingly common) unrelated
/// line before any regex runs.</para>
/// </summary>
public static partial class WordOfPowerDiscoveredParser
{
    // The second ProcessBook argument contains the discovery narrative.
    // Newlines inside the string are stored as literal two-char \n escapes
    // (backslash + n), not real newlines, so `\\n` in the pattern matches the
    // file bytes.
    [GeneratedRegex(
        """ProcessBook\("You discovered a word of power!",\s*"You've discovered a word of power: <sel>(?<code>[A-Z]+)</sel>.*?<b><size=125%>Word of Power: (?<effect>[^<]+)</size></b>\\n(?<desc>.+?)\\n\\n<i>""",
        RegexOptions.CultureInvariant)]
    private static partial Regex DiscoveryRx();

    /// <summary>
    /// Parse one classified LocalPlayer line body into a discovery frame
    /// payload, or return <c>null</c> if the line is not a Words-of-Power
    /// discovery. Pure function — no side effects, no clock reads.
    /// </summary>
    public static WordOfPowerDiscoveryFrame? TryParse(string data)
    {
        if (data is null) return null;
        if (!data.Contains("You discovered a word of power!", StringComparison.Ordinal))
            return null;

        var m = DiscoveryRx().Match(data);
        if (!m.Success) return null;

        return new WordOfPowerDiscoveryFrame(
            Code: m.Groups["code"].Value,
            EffectName: m.Groups["effect"].Value.Trim(),
            Description: m.Groups["desc"].Value.Trim());
    }
}
