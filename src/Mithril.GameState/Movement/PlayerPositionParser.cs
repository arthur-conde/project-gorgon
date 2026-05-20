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
///   first teleport. This branch is <b>gated on the <c>LocalPlayer:</c>
///   prefix</b> (boundary-checked, see <see cref="LocalAddPlayerRx"/>).
///   <b>Verified 2026-05-18</b> via a live busy-town capture (10&#160;MB,
///   4191 <c>Process*</c> lines, 25+ verbs): Player.log logs <em>only</em> the
///   local player's own processing — other players in view emit no
///   <c>ProcessAddPlayer</c> at all. The gate is thus effectively always-true
///   on real data (harmless, not load-bearing); kept as cheap defence in case
///   PG ever changes.</item>
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

    /// <summary>
    /// Parses <c>ProcessNewPosition</c> on a <see cref="LocalPlayerLogLine"/>
    /// payload (post-L0.5, envelope eaten). The <c>ProcessNewPosition</c>
    /// regex is actor-agnostic, so it matches bare verb data correctly.
    ///
    /// <para><c>ProcessAddPlayer</c> is NOT handled here — L0.5 routes it
    /// to <see cref="SystemSignalLogLine"/> { <see cref="SystemSignalKind.PlayerAdded"/> }
    /// instead, and consumers feed that body to
    /// <see cref="TryParseSpawnFromData"/>. See #556 Phase 3's
    /// <c>PlayerPositionTracker</c> switch.</para>
    /// </summary>
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

        return null;
    }

    /// <summary>
    /// Parses the spawn position from the L0.5-classified
    /// <see cref="SystemSignalLogLine"/> payload data — i.e. the
    /// <c>ProcessAddPlayer(…)</c> body with the <c>[ts] LocalPlayer: </c>
    /// envelope already eaten by L0.5 (#532). Skips the actor-token gate
    /// that <see cref="TryParse"/> uses, because L0.5's classifier
    /// (PlayerLogLineClassifier) already routes only <c>LocalPlayer: ProcessAddPlayer</c>
    /// to <see cref="SystemSignalKind.PlayerAdded"/> — the actor is
    /// guaranteed upstream, so re-checking here would be a redundant gate.
    ///
    /// <para>Used by the post-#556 unified-pipe migration of
    /// <see cref="PlayerPositionTracker"/>: when the tracker pattern-matches
    /// <c>SystemSignalLogLine { Kind: PlayerAdded }</c>, it feeds the
    /// envelope's <see cref="SystemSignalLogLine.Data"/> here to recover
    /// the spawn seed. <see cref="TryParse"/> remains the entry point for
    /// pre-L0.5 raw lines (full-line callers and the
    /// <see cref="ILogParser"/> interface contract).</para>
    /// </summary>
    public PlayerPositionEvent? TryParseSpawnFromData(string data, DateTime timestamp)
    {
        if (string.IsNullOrEmpty(data)) return null;
        if (!data.Contains("ProcessAddPlayer", StringComparison.Ordinal)) return null;
        if (AddPlayerPosRx().Match(data) is not { Success: true } m) return null;
        if (!TryNum(m.Groups["x"].ValueSpan, out var x)) return null;
        if (!TryNum(m.Groups["y"].ValueSpan, out var y)) return null;
        if (!TryNum(m.Groups["z"].ValueSpan, out var z)) return null;
        return new PlayerPositionEvent(timestamp, x, y, z, PlayerPositionSource.Spawn);
    }

    private static bool TryNum(ReadOnlySpan<char> s, out double v) =>
        double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out v);
}
