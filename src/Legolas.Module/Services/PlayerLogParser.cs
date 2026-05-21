using System.Globalization;
using System.Text.RegularExpressions;
using Mithril.Shared.Logging;
using Legolas.Domain;

namespace Legolas.Services;

/// <summary>
/// Player.log analog of <see cref="ChatLogParser"/> — pure line→event. #454:
/// <c>ProcessMapFx</c> (absolute survey/treasure-map targets); #488:
/// <c>ProcessDoDelayLoop</c> Motherlode-map use gesture; #604:
/// <c>ProcessScreenText(ImportantInfo, "The treasure is N meters from here.")</c>
/// Motherlode distance readout (migrated from <see cref="ChatLogParser"/> so the
/// coordinator becomes a single-source intra-PlayerWorld state machine). Map-pin
/// lifecycle parsing moved to the GameState-tier <c>MapPinParser</c> /
/// <c>PlayerPinTracker</c> (#468) — same promotion pattern as
/// <c>AreaTransitionParser</c> (#456).
///
/// <para>Real captured grammar (live Player.log, 2026-05-18):</para>
/// <code>
/// [08:25:39] LocalPlayer: ProcessMapFx((1236.00, 38.17, 2528.00), 25, 1, "Good Metal Slab is here", ImportantInfo, "The Good Metal Slab is 67m west and 1181m south.")
/// [22:09:22] LocalPlayer: ProcessScreenText(ImportantInfo, "The treasure is 1285 meters from here.")
/// </code>
/// The two middle integers on <c>ProcessMapFx</c> vary; that regex skips them
/// non-greedily and anchors on the leading coord triple plus the trailing
/// <c>"short", Category, "msg")</c> structure. Coordinates are <b>signed</b>
/// (negative X/Z are common — see <see cref="WorldCoord"/>).
///
/// <para>Post-#550 L1 migration: consumes the envelope-stripped
/// <see cref="LocalPlayerLogLine.Data"/> payload — L0.5 (#532) has already
/// classified the line as <c>LocalPlayer:</c>-actored and eaten the envelope,
/// so the per-line guards no longer re-anchor on it. Same pattern as the
/// GameState parsers post-#555.</para>
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
    // Post-#550: the LocalPlayer: actor envelope is eaten upstream by L0.5;
    // this guard no longer re-anchors on it.
    [GeneratedRegex(
        """ProcessDoDelayLoop\(\s*\d+(?:\.\d+)?\s*,\s*\w+\s*,\s*"(?<map>[^"]*Motherlode Map[^"]*)"\s*,""",
        RegexOptions.Compiled | RegexOptions.CultureInvariant | RegexOptions.IgnoreCase)]
    private static partial Regex MotherlodeUseRx();

    // #604: Motherlode distance readout — the player-facing banner emitted on
    // each measuring read of a carried Motherlode map. PG also mirrors this to
    // ChatLogs as "[Status] The treasure is N meters from here.", but the
    // Player.log emission lands ~1 s earlier and lets the coordinator pair the
    // distance with its use gesture from a single source (no cross-stream
    // ordering hazard). Accepts the US spelling ("meters") and the British
    // ("metres") for robustness. ImportantInfo is the only observed banner
    // namespace for this line; pin it so unrelated banners don't false-positive.
    [GeneratedRegex(
        """ProcessScreenText\(\s*ImportantInfo\s*,\s*"The treasure is (?<dist>\d+) met(?:er|re)s from here\.?"\s*\)""",
        RegexOptions.Compiled | RegexOptions.CultureInvariant)]
    private static partial Regex MotherlodeDistanceRx();

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

        // #604: ProcessScreenText banner — must be tried before ProcessMapFx
        // because ScreenText also carries an "ImportantInfo" category, but
        // ProcessMapFx is the parenthesised-coord form. Anchor on the literal
        // substring before the regex to keep the hot-path branch cheap.
        if (line.Contains("ProcessScreenText", StringComparison.Ordinal)
            && MotherlodeDistanceRx().Match(line) is { Success: true } d
            && int.TryParse(d.Groups["dist"].ValueSpan, out var metres))
        {
            return new MotherlodeDistance(timestamp, metres);
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
