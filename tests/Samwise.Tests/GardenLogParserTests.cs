using FluentAssertions;
using Samwise.Parsing;
using Xunit;

namespace Samwise.Tests;

public class GardenLogParserTests
{
    private readonly GardenLogParser _p = new();
    private static readonly DateTime T = new(2026, 4, 15, 12, 0, 0, DateTimeKind.Utc);

    [Fact]
    public void Parses_PlayerLogin_RealisticLine()
    {
        // Captured from a live Player.log: char name is the 2nd quoted arg.
        var line = @"[14:30:34] LocalPlayer: ProcessAddPlayer(-626625832, 290067, ""PlayerWolf(sex=m;race=e;@Hands=none)"", ""Emraell"", ""A player!"", System.String[], (1458, 41, 1404), (0, 0.87, 0, -0.49), Idle, Standing, 0, 2000, True)";
        var evt = _p.TryParse(line, T);
        evt.Should().BeOfType<PlayerLogin>().Which.CharName.Should().Be("Emraell");
    }

    [Fact]
    public void Ignores_RemotePlayerAdd()
    {
        // Remote player events lack the "LocalPlayer:" prefix.
        var line = @"ProcessAddPlayer(123, 456, ""OtherModel"", ""OtherPlayer"")";
        _p.TryParse(line, T).Should().BeNull();
    }

    [Fact]
    public void Parses_SetPetOwner()
    {
        var evt = _p.TryParse(@"LocalPlayer: ProcessSetPetOwner(12345, 999)", T);
        evt.Should().BeOfType<SetPetOwner>().Which.EntityId.Should().Be("12345");
    }

    [Fact]
    public void Parses_AppearanceLoop()
    {
        var evt = _p.TryParse(@"Download appearance loop @Carrot(scale=1.0)", T);
        var al = evt.Should().BeOfType<AppearanceLoop>().Subject;
        al.ModelName.Should().Be("Carrot");
        al.Scale.Should().Be(1.0);
    }

    [Fact]
    public void Parses_AppearanceLoop_SmallScale_FromRealisticLine()
    {
        // Newly-placed seed: small scale, suffix after the closing paren.
        var line = @"[16:22:58] Download appearance loop @Flower6(scale=0.1) is waiting on something";
        var evt = _p.TryParse(line, T);
        var al = evt.Should().BeOfType<AppearanceLoop>().Subject;
        al.ModelName.Should().Be("Flower6");
        al.Scale.Should().Be(0.1);
    }

    [Fact]
    public void Parses_UpdateDescription_WaterAction()
    {
        var line = @"ProcessUpdateDescription(12345, ""Onion"", ""thirsty plot"", ""Water Onion"", Summoned, ""OnionPlant(Scale=0.5)"", 0)";
        var evt = _p.TryParse(line, T);
        var ud = evt.Should().BeOfType<UpdateDescription>().Subject;
        ud.PlotId.Should().Be("12345");
        ud.Action.Should().Be("Water Onion");
        ud.Scale.Should().Be(0.5);
    }

    [Fact]
    public void Parses_StartInteraction_Summoned()
    {
        var line = @"... ProcessStartInteraction(12345, 0, 1.0, Summoned, ""SummonedCarrot"")";
        var evt = _p.TryParse(line, T);
        evt.Should().BeOfType<StartInteraction>().Which.PlotId.Should().Be("12345");
    }

    [Fact]
    public void Parses_AddItem_RealLogFormat()
    {
        // Real Player.log shape: ProcessAddItem(BarleySeeds(86940428), -1, False)
        var line = @"[20:48:30] LocalPlayer: ProcessAddItem(BarleySeeds(86940428), -1, False)";
        var evt = _p.TryParse(line, T);
        var ai = evt.Should().BeOfType<AddItem>().Subject;
        ai.ItemId.Should().Be("86940428");
        ai.ItemName.Should().Be("BarleySeeds");
    }

    [Fact]
    public void Parses_GardeningXp()
    {
        var line = @"... ProcessUpdateSkill(name=Gardening, type=Gardening, xp=100)";
        _p.TryParse(line, T).Should().BeOfType<GardeningXp>();
    }

    [Fact]
    public void Parses_ScreenTextError()
    {
        var line = @"... ProcessScreenText(""Plant is still growing"", ErrorMessage)";
        _p.TryParse(line, T).Should().BeOfType<ScreenTextError>();
    }

    [Fact]
    public void Returns_Null_For_UnrelatedLine()
    {
        _p.TryParse(@"some unrelated junk", T).Should().BeNull();
    }
}
