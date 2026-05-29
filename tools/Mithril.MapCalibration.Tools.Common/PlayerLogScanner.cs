using System.Globalization;
using System.Text.RegularExpressions;
using Mithril.MapCalibration;

namespace Mithril.Tools.MapCalibration.Common;

/// <summary>
/// Reads <c>Player.log</c> backwards looking for the most recent <c>[Status]</c>
/// line that records a position within the requested area. <c>[Status]</c> is
/// PG's periodic self-report giving the active character's world position +
/// area name (see memory <c>pg_log_timezones</c> — Player.log timestamps are
/// UTC, but the position fields are area-local coords).
///
/// <para>Used to skip the user having to type their <c>--player-coord</c> by
/// hand: the screenshot timestamp + the most-recent-status-in-area heuristic
/// will pick up the player's actual world position at capture time, which then
/// becomes the most reliable reference point for the solver via the player-pin
/// template match.</para>
/// </summary>
public static class PlayerLogScanner
{
    // [Status] lines vary across PG versions. The robust subset to extract:
    //   - the area name (often "Area: <Area...>" or embedded in a longer status)
    //   - the position as "x:N y:N z:N" via the canonical WorldCoord.TryParse
    //
    // Two regex variants cover the observed shapes; both pull (areaSpan, locSpan).
    private static readonly Regex StatusLine = new(
        @"\[Status\].*?Area:\s*(?<area>\w+).*?(?<loc>x:[-\d.]+\s+y:[-\d.]+\s+z:[-\d.]+)",
        RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    public static WorldCoord? MostRecentPositionInArea(string playerLogPath, string area)
    {
        if (!File.Exists(playerLogPath))
        {
            throw new UserFacingException($"Player.log not found: {playerLogPath}");
        }

        // Linear forward scan is fine — Player.log rolls over per session, so
        // even a long session is <100 MB and one pass is cheap relative to the
        // NCC stage that runs later. We keep the LAST match so trailing
        // [Status] lines win.
        WorldCoord? best = null;
        using var reader = new StreamReader(playerLogPath);
        string? line;
        while ((line = reader.ReadLine()) is not null)
        {
            var m = StatusLine.Match(line);
            if (!m.Success) continue;
            if (!string.Equals(m.Groups["area"].Value, area, StringComparison.OrdinalIgnoreCase)) continue;
            var parsed = WorldCoord.TryParse(m.Groups["loc"].Value);
            if (parsed is null) continue;
            best = parsed;
        }
        return best;
    }
}
