using System.Globalization;
using System.Text.RegularExpressions;
using Mithril.Shared.Logging;
using Legolas.Domain;

namespace Legolas.Services;

/// <summary>
/// Player.log analog of <see cref="ChatLogParser"/> — pure line→event. #454:
/// <c>ProcessMapFx</c> (absolute survey/treasure-map targets); #488:
/// <c>ProcessDoDelayLoop</c> Motherlode-map use gesture. Map-pin lifecycle
/// parsing moved to the GameState-tier <c>MapPinParser</c> /
/// <c>PlayerPinTracker</c> (#468) — same promotion pattern as
/// <c>AreaTransitionParser</c> (#456).
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

    // Motherlode-map use gesture: ProcessDoDelayLoop whose quoted action text
    // mentions a Motherlode map. The quoted text is captured for its map *type*
    // name (e.g. "Kur Mountains Simple Metal Motherlode Map") — a display label
    // only; binding stays order-based (the type is identical across a same-type
    // stack, no per-map identity). Grammar mirrors Gandalf's
    // InteractionDelayLoopParser (no cross-module dependency taken).
    [GeneratedRegex(
        """LocalPlayer:\s*ProcessDoDelayLoop\(\s*\d+(?:\.\d+)?\s*,\s*\w+\s*,\s*"(?<map>[^"]*Motherlode Map[^"]*)"\s*,""",
        RegexOptions.Compiled | RegexOptions.CultureInvariant | RegexOptions.IgnoreCase)]
    private static partial Regex MotherlodeUseRx();

    private static string? NormalizeMapName(string raw)
    {
        var s = raw.Trim();
        // PG action text is "Using <Item Name>"; strip the verb prefix so the
        // label is the bare map name.
        if (s.StartsWith("Using ", StringComparison.OrdinalIgnoreCase))
            s = s["Using ".Length..].Trim();
        return s.Length == 0 ? null : s;
    }

    public LogEvent? TryParse(string line, DateTime timestamp)
    {
        if (string.IsNullOrEmpty(line)) return null;

        if (line.Contains("ProcessDoDelayLoop(", StringComparison.Ordinal)
            && MotherlodeUseRx().Match(line) is { Success: true } use)
        {
            return new MotherlodeUseDetected(
                timestamp, NormalizeMapName(use.Groups["map"].Value));
        }

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

        return null;
    }

    private static bool TryNum(ReadOnlySpan<char> s, out double v) =>
        double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out v);
}
