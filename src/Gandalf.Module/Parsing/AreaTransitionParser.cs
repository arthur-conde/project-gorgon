using System.Text.RegularExpressions;
using Mithril.Shared.Logging;

namespace Gandalf.Parsing;

/// <summary>
/// Parses Project Gorgon's <c>LOADING LEVEL Area&lt;Name&gt;</c> log line into
/// an <see cref="AreaTransitionEvent"/>. Fires on initial spawn (after
/// character select) and on every zone transition during gameplay. The area
/// code matches <c>areas.json</c> keys exactly (e.g. <c>"AreaSerbule"</c>,
/// <c>"AreaEltibule"</c>, <c>"AreaTomb1"</c>).
///
/// Three discriminable forms (#178 wiki):
/// <list type="bullet">
///   <item><c>LOADING LEVEL Area&lt;Name&gt;</c> — real game area; emits a
///   non-null <see cref="AreaTransitionEvent.AreaKey"/>.</item>
///   <item><c>LOADING LEVEL ChooseCharacter</c> — character-select screen;
///   emits a null AreaKey so the tracker clears its current-area state (no
///   chest interactions can fire from this screen).</item>
///   <item><c>LOADING LEVEL </c> (empty) — disconnect; same null-AreaKey
///   handling as ChooseCharacter.</item>
/// </list>
///
/// Live capture (#178):
/// <code>
/// [17:11:10] LOADING LEVEL AreaSerbule
/// [17:28:06] LOADING LEVEL AreaEltibule
/// [18:30:35] LOADING LEVEL                  ← empty body on disconnect
/// [19:13:54] LOADING LEVEL ChooseCharacter
/// </code>
///
/// The line carries the <c>[HH:MM:SS]</c> timestamp prefix that
/// <see cref="PlayerLogTailReader"/> uses for sequencing but does not strip
/// from <see cref="RawLogLine.Line"/>. The regex is unanchored — same
/// convention as every other <c>Player.log</c> parser in the codebase
/// (<c>GardenLogParser</c>, <c>FavorLogParser</c>, <c>VendorLogParser</c>,
/// …) — and matches the <c>LOADING LEVEL</c> token wherever it appears,
/// pinning the area body to end-of-line so trailing junk can't slip in.
/// </summary>
public sealed partial class AreaTransitionParser : ILogParser
{
    [GeneratedRegex(
        """LOADING LEVEL\s*(?<area>\S*)\s*$""",
        RegexOptions.CultureInvariant)]
    private static partial Regex AreaRx();

    public LogEvent? TryParse(string line, DateTime timestamp)
    {
        if (!line.Contains("LOADING LEVEL", StringComparison.Ordinal)) return null;
        var m = AreaRx().Match(line);
        if (!m.Success) return null;

        var area = m.Groups["area"].Value;

        // ChooseCharacter and empty body both indicate "not in a real game
        // area"; emit null so the tracker clears.
        if (string.IsNullOrEmpty(area) ||
            string.Equals(area, "ChooseCharacter", StringComparison.Ordinal))
        {
            return new AreaTransitionEvent(timestamp, AreaKey: null);
        }

        // Real area names always start with "Area" by PG convention. Anything
        // else (defensive — unknown intermediate level loads) emits null too.
        if (!area.StartsWith("Area", StringComparison.Ordinal))
        {
            return new AreaTransitionEvent(timestamp, AreaKey: null);
        }

        return new AreaTransitionEvent(timestamp, AreaKey: area);
    }
}
