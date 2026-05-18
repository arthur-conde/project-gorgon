using System.Globalization;
using System.Text.RegularExpressions;
using Mithril.Shared.Logging;
using Legolas.Domain;

namespace Legolas.Services;

/// <summary>
/// Player.log analog of <see cref="ChatLogParser"/> — pure line→event. #454:
/// <c>ProcessMapFx</c> (absolute survey/treasure-map targets) and
/// <c>ProcessMapPinAdd</c> (freehand pin-calibration world coords). One
/// parser, many patterns, mirroring the ChatLog parser's shape.
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

    // ProcessMapPinAdd(1, 0, 0, (-521.96, 0.00, 368.39), "Calib 1")
    // Leading three ints + the (x, y, z) triple + quoted label. The label is
    // captured for diagnostics ONLY — pairing is turn-order, never by name.
    [GeneratedRegex(
        """ProcessMapPinAdd\(\s*\d+\s*,\s*\d+\s*,\s*\d+\s*,\s*\(\s*(?<x>-?\d+(?:\.\d+)?)\s*,\s*(?<y>-?\d+(?:\.\d+)?)\s*,\s*(?<z>-?\d+(?:\.\d+)?)\s*\)\s*,\s*"(?<label>[^"]*)"\s*\)""",
        RegexOptions.Compiled | RegexOptions.CultureInvariant)]
    private static partial Regex MapPinAddRx();

    public LogEvent? TryParse(string line, DateTime timestamp)
    {
        if (string.IsNullOrEmpty(line)) return null;

        if (line.Contains("ProcessMapFx", StringComparison.Ordinal)
            && MapFxRx().Match(line) is { Success: true } m
            && TryNum(m.Groups["x"].ValueSpan, out var x)
            && TryNum(m.Groups["y"].ValueSpan, out var y)
            && TryNum(m.Groups["z"].ValueSpan, out var z))
        {
            return new MapTargetDetected(
                timestamp,
                new WorldCoord(x, y, z),
                m.Groups["short"].Value,
                m.Groups["cat"].Value,
                m.Groups["msg"].Value);
        }

        if (line.Contains("ProcessMapPinAdd", StringComparison.Ordinal)
            && MapPinAddRx().Match(line) is { Success: true } p
            && TryNum(p.Groups["x"].ValueSpan, out var px)
            && TryNum(p.Groups["y"].ValueSpan, out var py)
            && TryNum(p.Groups["z"].ValueSpan, out var pz))
        {
            return new MapPinAdded(
                timestamp,
                new WorldCoord(px, py, pz),
                p.Groups["label"].Value);
        }

        return null;
    }

    private static bool TryNum(ReadOnlySpan<char> s, out double v) =>
        double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out v);
}
