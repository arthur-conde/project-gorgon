using System.Globalization;
using System.Text.RegularExpressions;
using Mithril.Shared.Logging;
using Legolas.Domain;

namespace Legolas.Services;

/// <summary>
/// Player.log line→event parser. #454: <c>ProcessMapFx</c> (absolute
/// survey/treasure-map targets); #488: <c>ProcessDoDelayLoop</c> Motherlode-map
/// use gesture; #604: <c>ProcessScreenText(ImportantInfo, "The treasure is N
/// meters from here.")</c> Motherlode distance readout (migrated from the chat
/// log so the coordinator becomes a single-source intra-PlayerWorld state
/// machine); #606: <c>ProcessScreenText(ImportantInfo, "&lt;Mineral&gt;
/// collected!")</c> survey collect readout (migrated from the chat log so
/// Legolas is now Player.log-sim-resident — closes the world-sim Phase 3 with
/// no remaining <see cref="Mithril.Shared.Logging.IChatLogStream"/> consumer
/// in the module). Map-pin lifecycle parsing moved to the GameState-tier
/// <c>MapPinParser</c> / <c>PlayerPinTracker</c> (#468) — same promotion
/// pattern as <c>AreaTransitionParser</c> (#456).
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

    // #606: ProcessScreenText survey-collect readout. Mirrors the chat
    // "[Status] <Mineral> collected!" line minus the [Status] prefix — same
    // optional speed-bonus tail ("Also found <Bonus> x<N> (speed bonus!)").
    // Anchored on ImportantInfo + the literal "collected!" so unrelated
    // banners don't false-positive; the trailing-period tolerance and
    // ImportantInfo discriminator follow MotherlodeDistanceRx's shape.
    //
    // Per the live capture (wiki Player-Log-Signals §Source — Player.log is
    // canonical; chat is a redundant mirror), the primary "collected!" line
    // carries NO count for the primary item (#699 then accepted this as a
    // structural property of the post-migration single-world attribution
    // path: ItemCollectionTracker credits one per matched (Add, Collect)
    // pair against IPlayerWorld.Bus<PlayerInventoryAdded> — no separate
    // stack-size composition surface). The (?:\s+x\d+)? before "collected!"
    // is kept for legacy/edge-case parity with the retired ChatLogParser.
    [GeneratedRegex(
        """ProcessScreenText\(\s*ImportantInfo\s*,\s*"(?<name>.+?)(?:\s+x\d+)?\s+collected!(?:\s+Also found\s+(?<bonus>.+?)(?:\s+x\d+)?\s+\(speed bonus!\))?\.?"\s*\)""",
        RegexOptions.Compiled | RegexOptions.CultureInvariant)]
    private static partial Regex ItemCollectedRx();

    // #606: ProcessMapFx trailing-arg relative-offset readout. Same line as
    // MapFxRx — PG embeds the chat-mirrored directional string ("The X is Nm
    // DIR and Mm DIR.") as the trailing string argument alongside the absolute
    // (X, Y, Z). The absolute coord drives pin placement (#454); the relative
    // offset drives the calibration verify-mode hook (NoteSurvey). One line,
    // two consumers — both Player.log-resident post-#606. Mirrors the chat-
    // retired SurveyRegex shape so the (a,aDir,b,bDir) composition is identical.
    [GeneratedRegex(
        """The (?<name>.+?) is (?<a>\d+)m (?<aDir>north|south|east|west) and (?<b>\d+)m (?<bDir>north|south|east|west)\b""",
        RegexOptions.Compiled | RegexOptions.CultureInvariant | RegexOptions.IgnoreCase)]
    private static partial Regex MapFxRelativeOffsetRx();

    /// <summary>
    /// Extract the relative-offset readout embedded in a <c>ProcessMapFx</c>
    /// trailing string ("The X is Nm DIR and Mm DIR."). Returns null when
    /// the message doesn't match (uncalibrated areas, atypical banners). The
    /// caller pairs this with the absolute (X,Y,Z) parsed by
    /// <see cref="TryParse"/> — both bits of data come from the same log
    /// line, so a paired emission is intra-line and order-free.
    /// </summary>
    public static MetreOffset? TryParseMapFxRelativeOffset(string message)
    {
        if (string.IsNullOrEmpty(message)) return null;
        var m = MapFxRelativeOffsetRx().Match(message);
        if (!m.Success
            || !int.TryParse(m.Groups["a"].ValueSpan, out var aValue)
            || !int.TryParse(m.Groups["b"].ValueSpan, out var bValue))
        {
            return null;
        }
        double east = 0, north = 0;
        ApplyComponent(m.Groups["aDir"].Value, aValue, ref east, ref north);
        ApplyComponent(m.Groups["bDir"].Value, bValue, ref east, ref north);
        return new MetreOffset(east, north);
    }

    private static void ApplyComponent(string direction, int value, ref double east, ref double north)
    {
        switch (direction.ToLowerInvariant())
        {
            case "east":  east  = value;  break;
            case "west":  east  = -value; break;
            case "north": north = value;  break;
            case "south": north = -value; break;
        }
    }

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

        // #604/#606: ProcessScreenText banners — must be tried before
        // ProcessMapFx because ScreenText also carries an "ImportantInfo"
        // category, but ProcessMapFx is the parenthesised-coord form. Anchor
        // on the literal substring before the regex to keep the hot-path
        // branch cheap. Order within the ProcessScreenText branch:
        //   1. MotherlodeDistance — pinned to the "The treasure is …" prefix,
        //      cannot collide with ItemCollected.
        //   2. ItemCollected — broader "<name> collected!" shape; tried after
        //      MotherlodeDistance so the more specific regex wins.
        if (line.Contains("ProcessScreenText", StringComparison.Ordinal))
        {
            if (MotherlodeDistanceRx().Match(line) is { Success: true } d
                && int.TryParse(d.Groups["dist"].ValueSpan, out var metres))
            {
                return new MotherlodeDistance(timestamp, metres);
            }
            if (ItemCollectedRx().Match(line) is { Success: true } ic)
            {
                string? bonus = null;
                if (ic.Groups["bonus"].Success)
                    bonus = ic.Groups["bonus"].Value.Trim();
                return new ItemCollected(
                    timestamp,
                    ic.Groups["name"].Value.Trim(),
                    Count: 1,
                    SpeedBonusItem: bonus);
            }
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
