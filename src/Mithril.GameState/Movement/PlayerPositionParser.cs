using System.Globalization;
using System.Text.RegularExpressions;
using Mithril.Shared.Logging;

namespace Mithril.GameState.Movement;

/// <summary>
/// Parses Project Gorgon's <c>LocalPlayer: ProcessNewPosition((X, Y, Z), …)</c>
/// line into a <see cref="PlayerPositionEvent"/>. Emitted on teleport,
/// zone-in, and certain combat blinks — sparse, not a per-tick feed.
///
/// <para>Real captured grammar (live Player.log, 2026-05-18):</para>
/// <code>
/// [10:45:47] LocalPlayer: ProcessNewPosition((834.09, 290.24, 3480.81), (0.00000, 0.99849, 0.00000, 0.05489), Walk, OnLand, UseTeleportationCircle, Looping, 0, False, True, 1779101147245, 23980462)
/// [11:10:39] LocalPlayer: ProcessNewPosition((790.06, 309.18, 3386.07), (0.00000, -0.99973, 0.00000, -0.02334), Run, OnLand, Attack_Vampire_Teleport, InCombat, 23989952, False, True, 1779102639025, 23980462)
/// </code>
/// Only the leading <c>(X, Y, Z)</c> triple is consumed; the trailing
/// quaternion, move mode, action, etc. are skipped. Coordinates are
/// <b>signed</b> (negative X/Z are common). The regex is unanchored —
/// the same convention as every other <c>Player.log</c> parser in the
/// codebase — with a <c>ProcessNewPosition</c> substring fast-path so
/// unrelated lines return null without touching the engine.
/// </summary>
public sealed partial class PlayerPositionParser : ILogParser
{
    [GeneratedRegex(
        """ProcessNewPosition\(\(\s*(?<x>-?\d+(?:\.\d+)?)\s*,\s*(?<y>-?\d+(?:\.\d+)?)\s*,\s*(?<z>-?\d+(?:\.\d+)?)\s*\)""",
        RegexOptions.Compiled | RegexOptions.CultureInvariant)]
    private static partial Regex PositionRx();

    public LogEvent? TryParse(string line, DateTime timestamp)
    {
        if (string.IsNullOrEmpty(line)) return null;
        if (!line.Contains("ProcessNewPosition", StringComparison.Ordinal)) return null;

        var m = PositionRx().Match(line);
        if (!m.Success) return null;
        if (!TryNum(m.Groups["x"].ValueSpan, out var x) ||
            !TryNum(m.Groups["y"].ValueSpan, out var y) ||
            !TryNum(m.Groups["z"].ValueSpan, out var z))
            return null;

        return new PlayerPositionEvent(timestamp, x, y, z);
    }

    private static bool TryNum(ReadOnlySpan<char> s, out double v) =>
        double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out v);
}
