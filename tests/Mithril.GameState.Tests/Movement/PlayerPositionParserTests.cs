using FluentAssertions;
using Mithril.GameState.Movement;
using Xunit;

namespace Mithril.GameState.Tests.Movement;

public sealed class PlayerPositionParserTests
{
    private static readonly PlayerPositionParser Parser = new();
    private static readonly DateTime Ts = new(2026, 5, 18, 10, 45, 47, DateTimeKind.Utc);

    // Real captured lines (live Player.log, 2026-05-18).
    private const string TeleportLine =
        "[10:45:47] LocalPlayer: ProcessNewPosition((834.09, 290.24, 3480.81), (0.00000, 0.99849, 0.00000, 0.05489), Walk, OnLand, UseTeleportationCircle, Looping, 0, False, True, 1779101147245, 23980462)";

    private const string CombatBlinkLine =
        "[11:10:39] LocalPlayer: ProcessNewPosition((790.06, 309.18, 3386.07), (0.00000, -0.99973, 0.00000, -0.02334), Run, OnLand, Attack_Vampire_Teleport, InCombat, 23989952, False, True, 1779102639025, 23980462)";

    // Faithfully shaped ProcessAddPlayer: nested parens inside the quoted
    // appearance blob, position triple after the invariant System.String[] arg.
    private const string LocalAddPlayerLine =
        "[10:30:45] LocalPlayer: ProcessAddPlayer(1156406193, 23980462, \"@Base2-m(sex=m;Face=@eq-x(a=1);Hair=@eq(b=2))\", \"Emraell\", \"A player!\", System.String[], (787.86, 305.22, 3427.55), (0.00000, -0.99695, 0.00000, 0.07809), Idle, Standing, 0, 0, True)";

    [Fact]
    public void Parses_real_teleport_line_as_Movement()
    {
        var evt = Parser.TryParse(TeleportLine, Ts).Should().BeOfType<PlayerPositionEvent>().Subject;
        evt.X.Should().Be(834.09);
        evt.Y.Should().Be(290.24);
        evt.Z.Should().Be(3480.81);
        evt.Source.Should().Be(PlayerPositionSource.Movement);
        evt.Timestamp.Should().Be(Ts);
    }

    [Fact]
    public void Parses_real_combat_blink_line_as_Movement()
    {
        var evt = Parser.TryParse(CombatBlinkLine, Ts).Should().BeOfType<PlayerPositionEvent>().Subject;
        evt.X.Should().Be(790.06);
        evt.Z.Should().Be(3386.07);
        evt.Source.Should().Be(PlayerPositionSource.Movement);
    }

    [Fact]
    public void Parses_local_ProcessAddPlayer_as_Spawn_anchoring_past_appearance_blob()
    {
        var evt = Parser.TryParse(LocalAddPlayerLine, Ts).Should().BeOfType<PlayerPositionEvent>().Subject;
        evt.X.Should().Be(787.86);
        evt.Y.Should().Be(305.22);
        evt.Z.Should().Be(3427.55);
        evt.Source.Should().Be(PlayerPositionSource.Spawn);
    }

    [Fact]
    public void Ignores_ProcessAddPlayer_for_other_players()
    {
        // Same shape, different actor — must NOT track strangers entering view.
        const string stranger =
            "[10:30:45] Bob: ProcessAddPlayer(1, 2, \"@Base()\", \"Bob\", \"A player!\", System.String[], (1.0, 2.0, 3.0), (0,0,0,1), Idle, Standing, 0, 0, True)";
        Parser.TryParse(stranger, Ts).Should().BeNull();
    }

    [Fact]
    public void Ignores_ProcessAddPlayer_with_no_actor_prefix()
    {
        const string noPrefix =
            "[10:30:45] ProcessAddPlayer(1, 2, \"@Base()\", \"X\", \"A player!\", System.String[], (1.0, 2.0, 3.0), (0,0,0,1), Idle, Standing, 0, 0, True)";
        Parser.TryParse(noPrefix, Ts).Should().BeNull();
    }

    [Fact]
    public void Preserves_signed_negative_coordinates()
    {
        const string line =
            "LocalPlayer: ProcessNewPosition((-512.50, -3.00, -1840.75), (0,0,0,1), Walk, OnLand, Zone, Looping, 0, False, True, 1, 2)";
        var evt = Parser.TryParse(line, Ts).Should().BeOfType<PlayerPositionEvent>().Subject;
        evt.X.Should().Be(-512.50);
        evt.Y.Should().Be(-3.00);
        evt.Z.Should().Be(-1840.75);
    }

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
