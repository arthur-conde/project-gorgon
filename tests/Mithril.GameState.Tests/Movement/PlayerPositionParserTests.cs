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

    [Fact]
    public void Every_real_line_parses_with_the_right_source()
    {
        var lines = FixtureLines();
        // Sanity: the fixture is the real corpus we think it is.
        lines.Count(l => l.Contains("ProcessAddPlayer", StringComparison.Ordinal)).Should().Be(12);
        lines.Count(l => l.Contains("ProcessNewPosition", StringComparison.Ordinal)).Should().Be(3);

        foreach (var line in lines)
        {
            var evt = Parser.TryParse(line, Ts).Should().BeOfType<PlayerPositionEvent>(
                because: $"every real position line must parse: {line[..Math.Min(60, line.Length)]}…").Subject;

            var expected = line.Contains("ProcessAddPlayer", StringComparison.Ordinal)
                ? PlayerPositionSource.Spawn
                : PlayerPositionSource.Movement;
            evt.Source.Should().Be(expected);

            // Coords are real game values — never all-zero, always finite.
            (evt.X == 0 && evt.Y == 0 && evt.Z == 0).Should().BeFalse();
            double.IsFinite(evt.X).Should().BeTrue();
        }
    }

    [Fact]
    public void Real_ProcessAddPlayer_spot_check_positive_coords()
    {
        // [10:30:45] … System.String[], (787.86, 305.22, 3427.55), …
        var evt = Parser.TryParse(LineAt("[10:30:45]"), Ts)
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
        var evt = Parser.TryParse(LineAt("[08:22:20]"), Ts)
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
    // SYNTHETIC. Across every available capture (3 Player.log files, Apr–May
    // 2026, ~7700 Process* lines, 13 ProcessAddPlayer) EVERY line is
    // `LocalPlayer:`-prefixed — Player.log appears to log only the local
    // player's own client processing. We have NO real other-player
    // ProcessAddPlayer line. The `LocalPlayer:` gate is a defensive guard
    // against a non-local format we have not observed; the fixture below
    // enumerates several *assumed* shapes (each with a valid position triple,
    // so the GATE — not a parse failure — must be what rejects them).
    // VERIFICATION OWED: capture a populated-area Player.log.

    public static IEnumerable<object[]> AssumedNonLocalLines() =>
        File.ReadAllLines(Path.Combine(
                AppContext.BaseDirectory, "Movement", "Fixtures",
                "synthetic_nonlocal_addplayer_assumed.log"))
            .Select(l => l.Trim())
            .Where(l => l.Length > 0 && !l.StartsWith('#'))
            .Select(l => new object[] { l });

    [Theory]
    [MemberData(nameof(AssumedNonLocalLines))]
    public void Synthetic_rejects_assumed_non_local_ProcessAddPlayer_shapes(string line)
    {
        // Sanity: the line WOULD parse if the gate weren't doing the work
        // (it carries a real-shaped `System.String[], (x, y, z)` triple).
        line.Should().Contain("System.String[], (");
        Parser.TryParse(line, Ts).Should().BeNull(
            because: $"only `LocalPlayer: ProcessAddPlayer` is ours: {line}");
    }

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
