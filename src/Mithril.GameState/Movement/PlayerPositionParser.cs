using System.Globalization;
using System.Text.RegularExpressions;
using Mithril.Shared.Logging;

namespace Mithril.GameState.Movement;

/// <summary>
/// Parses the local player's own world position from Project Gorgon's
/// <c>Player.log</c>. Two lines carry it:
///
/// <list type="bullet">
///   <item><b><c>ProcessNewPosition</c></b> — emitted on teleport / zone-in /
///   some combat blinks (sparse). The line's first-person state fields
///   (move mode, <c>UseTeleportationCircle</c>, <c>Attack_*_Teleport</c>, …)
///   make it local-player-only by game semantics.</item>
///   <item><b><c>LocalPlayer: ProcessAddPlayer</c></b> — the player being
///   added to the scene at login / zone-in. This is the line the live replay
///   window is <em>seeded to</em>, so it is observed at session start — it
///   populates position immediately rather than leaving it null until the
///   first teleport. <c>ProcessAddPlayer</c> also fires for <em>other</em>
///   players entering view, so this branch is <b>gated on the
///   <c>LocalPlayer:</c> prefix</b> — without that guard the tracker would
///   follow strangers.</item>
/// </list>
///
/// <para>Real captured grammar (live Player.log, 2026-05-18):</para>
/// <code>
/// [10:45:47] LocalPlayer: ProcessNewPosition((834.09, 290.24, 3480.81), (0,…), Walk, OnLand, UseTeleportationCircle, …)
/// [10:30:45] LocalPlayer: ProcessAddPlayer(1156406193, 23980462, "@Base2-m(…huge appearance blob…)", "Emraell", "A player!", System.String[], (787.86, 305.22, 3427.55), (0,…), Idle, Standing, 0, 0, True)
/// </code>
/// <c>ProcessNewPosition</c> puts the triple right after the token;
/// <c>ProcessAddPlayer</c> buries it after a huge appearance string, so it is
/// anchored on the invariant <c>System.String[]</c> argument (the serialized
/// form of the abilities string-array — always the type name, never its
/// contents) that immediately precedes the position triple. Coordinates are
/// <b>signed</b>; unrelated lines fast-path to null.
/// </summary>
public sealed partial class PlayerPositionParser : ILogParser
{
    [GeneratedRegex(
        """ProcessNewPosition\(\(\s*(?<x>-?\d+(?:\.\d+)?)\s*,\s*(?<y>-?\d+(?:\.\d+)?)\s*,\s*(?<z>-?\d+(?:\.\d+)?)\s*\)""",
        RegexOptions.Compiled | RegexOptions.CultureInvariant)]
    private static partial Regex NewPositionRx();

    [GeneratedRegex(
        """System\.String\[\]\s*,\s*\(\s*(?<x>-?\d+(?:\.\d+)?)\s*,\s*(?<y>-?\d+(?:\.\d+)?)\s*,\s*(?<z>-?\d+(?:\.\d+)?)\s*\)""",
        RegexOptions.Compiled | RegexOptions.CultureInvariant)]
    private static partial Regex AddPlayerPosRx();

    public LogEvent? TryParse(string line, DateTime timestamp)
    {
        if (string.IsNullOrEmpty(line)) return null;

        if (line.Contains("ProcessNewPosition", StringComparison.Ordinal) &&
            NewPositionRx().Match(line) is { Success: true } m1 &&
            TryNum(m1.Groups["x"].ValueSpan, out var x1) &&
            TryNum(m1.Groups["y"].ValueSpan, out var y1) &&
            TryNum(m1.Groups["z"].ValueSpan, out var z1))
        {
            return new PlayerPositionEvent(timestamp, x1, y1, z1, PlayerPositionSource.Movement);
        }

        // ProcessAddPlayer fires for every player entering view — only the
        // local player's own line is ours. Prefix-gate, then anchor on the
        // System.String[] arg that precedes the position triple.
        if (line.Contains("LocalPlayer: ProcessAddPlayer", StringComparison.Ordinal) &&
            AddPlayerPosRx().Match(line) is { Success: true } m2 &&
            TryNum(m2.Groups["x"].ValueSpan, out var x2) &&
            TryNum(m2.Groups["y"].ValueSpan, out var y2) &&
            TryNum(m2.Groups["z"].ValueSpan, out var z2))
        {
            return new PlayerPositionEvent(timestamp, x2, y2, z2, PlayerPositionSource.Spawn);
        }

        return null;
    }

    private static bool TryNum(ReadOnlySpan<char> s, out double v) =>
        double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out v);
}
