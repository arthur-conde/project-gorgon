using FluentAssertions;
using Samwise.Parsing;
using Xunit;

namespace Samwise.Tests;

public class GardenLogParserTests
{
    private readonly GardenLogParser _p = new();
    private static readonly DateTime T = new(2026, 4, 15, 12, 0, 0, DateTimeKind.Utc);

    [Fact]
    public void Parses_PlayerLogin()
    {
        var evt = _p.TryParse(@"... LocalPlayer: ProcessAddPlayer(""Hitsuzen""); ...", T);
        evt.Should().BeOfType<PlayerLogin>().Which.CharName.Should().Be("Hitsuzen");
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
        evt.Should().BeOfType<AppearanceLoop>().Which.ModelName.Should().Be("Carrot");
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
    public void Parses_AddItem()
    {
        var line = @"ProcessAddItem(99, ""Carrot""...)";
        var evt = _p.TryParse(line, T);
        var ai = evt.Should().BeOfType<AddItem>().Subject;
        ai.ItemId.Should().Be("99");
        ai.ItemName.Should().Be("Carrot");
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
