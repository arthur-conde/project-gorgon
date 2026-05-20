using System.IO;
using FluentAssertions;
using Mithril.GameState.Movement;
using Xunit;

namespace Mithril.GameState.Tests.Movement;

public sealed class PlayerPositionParserTests
{
    private static readonly PlayerPositionParser Parser = new();
    private static readonly DateTime Ts = new(2026, 5, 18, 10, 45, 47, DateTimeKind.Utc);

    private static readonly string FixturePath = Path.Combine(
        AppContext.BaseDirectory, "Movement", "Fixtures", "real_player_log_position_lines.log");

    // Byte-exact lines pulled from a real Player.log (this machine, Apr–May
    // 2026): 12 distinct ProcessAddPlayer + 3 ProcessNewPosition, each with
    // the genuine ~1.5 KB appearance blob (nested ()/{}/[]/@/^/& and all).
    private static string[] FixtureLines() =>
        File.ReadAllLines(FixturePath)
            .Where(l => l.Length > 0)
            .ToArray();

    private static string LineAt(string tsPrefix) =>
        FixtureLines().Single(l => l.StartsWith(tsPrefix, StringComparison.Ordinal));

    /// <summary>
    /// Strip the <c>[HH:MM:SS] LocalPlayer: </c> envelope to recover the bare
    /// <c>ProcessAddPlayer(…)</c> body — the shape L0.5 produces for the
    /// <see cref="Mithril.Shared.Logging.SystemSignalKind.PlayerAdded"/> pipe
    /// (Phase 3 / #569). The fixture lines are raw Player.log lines, so
    /// tests that route through <see cref="PlayerPositionParser.TryParseSpawnFromData"/>
    /// strip the envelope first to match the production callsite shape.
    /// </summary>
    private static string EatLocalPlayerEnvelope(string line)
    {
        const string Marker = "LocalPlayer: ";
        var idx = line.IndexOf(Marker, StringComparison.Ordinal);
        return idx >= 0 ? line.Substring(idx + Marker.Length) : line;
    }

    [Fact]
    public void Every_real_line_parses_with_the_right_source()
    {
        var lines = FixtureLines();
        // Sanity: the fixture is the real corpus we think it is.
        lines.Count(l => l.Contains("ProcessAddPlayer", StringComparison.Ordinal)).Should().Be(12);
        lines.Count(l => l.Contains("ProcessNewPosition", StringComparison.Ordinal)).Should().Be(3);

        foreach (var line in lines)
        {
            // Route through the production entry point for each verb class:
            //   * ProcessNewPosition → TryParse (LocalPlayer pipe in
            //     production; the regex is actor-agnostic so it works on
            //     bare verb data or on a full raw line).
            //   * ProcessAddPlayer → TryParseSpawnFromData (SystemSignal
            //     pipe in production; L0.5 eats the LocalPlayer: envelope,
            //     so the test feeds the bare body).
            var isSpawn = line.Contains("ProcessAddPlayer", StringComparison.Ordinal);
            var evt = (isSpawn
                ? Parser.TryParseSpawnFromData(EatLocalPlayerEnvelope(line), Ts)
                : Parser.TryParse(line, Ts) as PlayerPositionEvent)
                .Should().BeOfType<PlayerPositionEvent>(
                    because: $"every real position line must parse: {line[..Math.Min(60, line.Length)]}…").Subject;

            evt.Source.Should().Be(isSpawn
                ? PlayerPositionSource.Spawn
                : PlayerPositionSource.Movement);

            // Coords are real game values — never all-zero, always finite.
            (evt.X == 0 && evt.Y == 0 && evt.Z == 0).Should().BeFalse();
            double.IsFinite(evt.X).Should().BeTrue();
        }
    }

    [Fact]
    public void Real_ProcessAddPlayer_spot_check_positive_coords()
    {
        // [10:30:45] … System.String[], (787.86, 305.22, 3427.55), …
        var evt = Parser.TryParseSpawnFromData(EatLocalPlayerEnvelope(LineAt("[10:30:45]")), Ts)
            .Should().BeOfType<PlayerPositionEvent>().Subject;
        evt.X.Should().Be(787.86);
        evt.Y.Should().Be(305.22);
        evt.Z.Should().Be(3427.55);
        evt.Source.Should().Be(PlayerPositionSource.Spawn);
    }

    [Fact]
    public void Real_ProcessAddPlayer_spot_check_signed_negative_coords()
    {
        // [08:22:20] … System.String[], (-504.29, -42.05, -648.84), … —
        // proves signed handling against genuine game data, not a synthetic.
        var evt = Parser.TryParseSpawnFromData(EatLocalPlayerEnvelope(LineAt("[08:22:20]")), Ts)
            .Should().BeOfType<PlayerPositionEvent>().Subject;
        evt.X.Should().Be(-504.29);
        evt.Y.Should().Be(-42.05);
        evt.Z.Should().Be(-648.84);
        evt.Source.Should().Be(PlayerPositionSource.Spawn);
    }

    [Fact]
    public void Real_ProcessNewPosition_spot_check()
    {
        // [10:45:47] LocalPlayer: ProcessNewPosition((834.09, 290.24, 3480.81), …
        var evt = Parser.TryParse(LineAt("[10:45:47]"), Ts)
            .Should().BeOfType<PlayerPositionEvent>().Subject;
        evt.X.Should().Be(834.09);
        evt.Y.Should().Be(290.24);
        evt.Z.Should().Be(3480.81);
        evt.Source.Should().Be(PlayerPositionSource.Movement);
    }

    // --- Other-player exclusion -------------------------------------------
    // The previous SYNTHETIC NonLocalPlayer guard test was retired with the
    // ProcessAddPlayer branch of TryParse (#556 follow-up). The actor
    // boundary check now lives upstream in
    // PlayerLogLineClassifier.Classify's literal "LocalPlayer: " prefix
    // match — the L0.5 classifier never routes a non-LocalPlayer actor
    // line to the SystemSignal { PlayerAdded } pipe, so by the time
    // TryParseSpawnFromData sees data, the actor has already been
    // structurally verified upstream. PlayerLogLineClassifierTests covers
    // the upstream check.

    // --- Negative / unrelated ---------------------------------------------

    [Theory]
    [InlineData("")]
    [InlineData("[10:45:47] LocalPlayer: ProcessAddItem(Apple(1), -1, True)")]
    [InlineData("[10:45:47] LocalPlayer: ProcessMapFx((1236.00, 38.17, 2528.00), 25, 1, \"x\", ImportantInfo, \"y\")")]
    [InlineData("LOADING LEVEL AreaSerbule")]
    public void Returns_null_for_unrelated_lines(string line)
    {
        Parser.TryParse(line, Ts).Should().BeNull();
    }

    [Fact]
    public void Returns_null_when_token_present_but_shape_malformed()
    {
        Parser.TryParse("LocalPlayer: ProcessNewPosition(garbage no coords)", Ts).Should().BeNull();
    }
}
