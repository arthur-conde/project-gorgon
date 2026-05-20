using System.Text.RegularExpressions;
using Mithril.Shared.Logging;

namespace Mithril.GameState.Celestial.Parsing;

/// <summary>
/// Parses the local player's lunar phase from Project Gorgon's
/// <c>Player.log</c>. Consumes the envelope-stripped
/// <see cref="LocalPlayerLogLine.Data"/> payload (post-#550 L1 migration) —
/// the L0.5 router (#532) has already classified the line as
/// <c>LocalPlayer:</c>-actored and eaten the envelope, so this parser sees
/// the bare <c>Verb(args)</c> shape:
///
/// <code>
/// ProcessSetCelestialInfo(WaxingCrescentMoon)
/// </code>
///
/// <para>The single argument is a phase token of one of three observed
/// spellings; mapping to a canonical <see cref="MoonPhase"/> is total
/// (unrecognised ⇒ <see cref="MoonPhase.Unknown"/>, raw token retained).
/// Unrelated lines fast-path to <c>null</c>; the parser never throws.</para>
/// </summary>
public sealed partial class CelestialLogParser : ILogParser
{
    [GeneratedRegex(
        """ProcessSetCelestialInfo\(\s*(?<phase>[A-Za-z]+)\s*\)""",
        RegexOptions.Compiled | RegexOptions.CultureInvariant)]
    private static partial Regex CelestialRx();

    public LogEvent? TryParse(string line, DateTime timestamp)
    {
        if (string.IsNullOrEmpty(line)) return null;
        if (!line.Contains("ProcessSetCelestialInfo", StringComparison.Ordinal)) return null;

        if (CelestialRx().Match(line) is not { Success: true } m) return null;

        var raw = m.Groups["phase"].Value;
        return new CelestialInfoEvent(timestamp, MoonPhaseExtensions.ParsePhase(raw), raw);
    }
}
