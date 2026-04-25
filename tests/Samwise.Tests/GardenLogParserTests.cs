using FluentAssertions;
using Samwise.Parsing;
using Xunit;

namespace Samwise.Tests;

public class GardenLogParserTests
{
    private readonly GardenLogParser _p = new();
    private static readonly DateTime T = new(2026, 4, 15, 12, 0, 0, DateTimeKind.Utc);

    [Fact]
    public void Ignores_ProcessAddPlayer_Lines()
    {
        // Player-login parsing now lives in ActiveCharacterLogSynchronizer; the
        // garden parser must return null for these lines to avoid doubly firing.
        var line = @"[14:30:34] LocalPlayer: ProcessAddPlayer(-626625832, 290067, ""PlayerWolf(sex=m;race=e;@Hands=none)"", ""Emraell"", ""A player!"", System.String[], (1458, 41, 1404), (0, 0.87, 0, -0.49), Idle, Standing, 0, 2000, True)";
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
    public void Ignores_ProcessAddItem()
    {
        // ProcessAddItem is now sourced from IInventoryService.ItemAdded; the garden
        // parser must ignore the raw line so it isn't double-processed.
        var line = @"[20:48:30] LocalPlayer: ProcessAddItem(BarleySeeds(86940428), -1, False)";
        _p.TryParse(line, T).Should().BeNull();
    }

    [Fact]
    public void Ignores_ProcessDeleteItem()
    {
        // Same as ProcessAddItem — sourced from IInventoryService.ItemDeleted.
        var line = @"[20:48:31] LocalPlayer: ProcessDeleteItem(86940428)";
        _p.TryParse(line, T).Should().BeNull();
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

    [Fact]
    public void Parses_PlantingCapReached_FromRealLogLine()
    {
        // Captured from a live Player.log.
        var line = @"[10:26:30] LocalPlayer: ProcessErrorMessage(ItemUnusable, ""Barley Seeds can't be used: You already have the maximum of that type of plant growing"")";
        var evt = _p.TryParse(line, T);
        evt.Should().BeOfType<PlantingCapReached>()
            .Which.SeedDisplayName.Should().Be("Barley Seeds");
    }

    [Fact]
    public void Ignores_OtherItemUnusableErrors()
    {
        // Different ItemUnusable messages shouldn't match the slot-cap pattern.
        // Currently no other pattern exists for these so the parser returns null.
        var line = @"[10:26:30] LocalPlayer: ProcessErrorMessage(ItemUnusable, ""You don't have enough water!"")";
        _p.TryParse(line, T).Should().BeNull();
    }
}
