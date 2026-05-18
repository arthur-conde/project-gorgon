using System.Globalization;
using System.Text.RegularExpressions;
using Mithril.Shared.Logging;
using Legolas.Domain;

namespace Legolas.Services;

/// <summary>
/// Player.log analog of <see cref="ChatLogParser"/> — pure line→event. #454
/// Phase 3 parses <c>ProcessMapFx</c> (absolute survey/treasure-map targets);
/// Phase 4 adds <c>ProcessMapPinAdd</c> on this same parser. One parser, many
/// patterns, mirroring the ChatLog parser's shape.
///
/// <para>Real captured grammar (live Player.log, 2026-05-18):</para>
/// <code>
/// [08:25:39] LocalPlayer: ProcessMapFx((1236.00, 38.17, 2528.00), 25, 1, "Good Metal Slab is here", ImportantInfo, "The Good Metal Slab is 67m west and 1181m south.")
/// </code>
/// The two middle integers vary; the regex skips them non-greedily and anchors
/// on the leading coord triple plus the trailing <c>"short", Category,
/// "msg")</c> structure. Coordinates are <b>signed</b> (negative X/Z are
/// common — see <see cref="WorldCoord"/>). Lines that aren't
/// <c>ProcessMapFx</c> (incl. Motherlode's <c>ProcessScreenText</c>) fast-path
/// to null, so Motherlode is excluded with no special-casing.
/// </summary>
public sealed partial class PlayerLogParser : ILogParser
{
    [GeneratedRegex(
        """ProcessMapFx\(\(\s*(?<x>-?\d+(?:\.\d+)?)\s*,\s*(?<y>-?\d+(?:\.\d+)?)\s*,\s*(?<z>-?\d+(?:\.\d+)?)\s*\)\s*,.*?,\s*"(?<short>[^"]*)"\s*,\s*(?<cat>[A-Za-z]+)\s*,\s*"(?<msg>[^"]*)"\s*\)""",
        RegexOptions.Compiled | RegexOptions.CultureInvariant)]
    private static partial Regex MapFxRx();

    public LogEvent? TryParse(string line, DateTime timestamp)
    {
        if (string.IsNullOrEmpty(line)) return null;
        if (!line.Contains("ProcessMapFx", StringComparison.Ordinal)) return null;

        var m = MapFxRx().Match(line);
        if (!m.Success) return null;
        if (!TryNum(m.Groups["x"].ValueSpan, out var x) ||
            !TryNum(m.Groups["y"].ValueSpan, out var y) ||
            !TryNum(m.Groups["z"].ValueSpan, out var z))
            return null;

        return new MapTargetDetected(
            timestamp,
            new WorldCoord(x, y, z),
            m.Groups["short"].Value,
            m.Groups["cat"].Value,
            m.Groups["msg"].Value);
    }

    private static bool TryNum(ReadOnlySpan<char> s, out double v) =>
        double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out v);
}
