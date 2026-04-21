using Arwen.Parsing;
using FluentAssertions;
using Xunit;

namespace Arwen.Tests;

public sealed class FavorLogParserTests
{
    private readonly FavorLogParser _parser = new();

    [Fact]
    public void ParsesStartInteraction()
    {
        var line = "LocalPlayer: ProcessStartInteraction(12345, 0, 547.5, True, \"NPC_Marna\")";
        var evt = _parser.TryParse(line, DateTime.UtcNow);

        evt.Should().BeOfType<FavorUpdate>();
        var update = (FavorUpdate)evt!;
        update.NpcKey.Should().Be("NPC_Marna");
        update.AbsoluteFavor.Should().BeApproximately(547.5, 0.01);
    }

    [Fact]
    public void ParsesNegativeFavor()
    {
        var line = "LocalPlayer: ProcessStartInteraction(999, 0, -150.3, False, \"NPC_Gretchen\")";
        var evt = _parser.TryParse(line, DateTime.UtcNow);

        evt.Should().BeOfType<FavorUpdate>();
        var update = (FavorUpdate)evt!;
        update.NpcKey.Should().Be("NPC_Gretchen");
        update.AbsoluteFavor.Should().BeApproximately(-150.3, 0.01);
    }

    [Fact]
    public void ParsesPlayerLogin()
    {
        var line = "LocalPlayer: ProcessAddPlayer(42, 100, \"HumanMale\", \"Emraell_Laeth\", 1)";
        var evt = _parser.TryParse(line, DateTime.UtcNow);

        evt.Should().BeOfType<FavorPlayerLogin>();
        var login = (FavorPlayerLogin)evt!;
        login.CharName.Should().Be("Emraell_Laeth");
    }

    [Fact]
    public void ParsesDeltaFavor()
    {
        var line = "[18:14:21] LocalPlayer: ProcessDeltaFavor(0, \"NPC_Fainor\", 23, True)";
        var evt = _parser.TryParse(line, DateTime.UtcNow);

        evt.Should().BeOfType<FavorDelta>();
        var delta = (FavorDelta)evt!;
        delta.NpcKey.Should().Be("NPC_Fainor");
        delta.Delta.Should().Be(23);
    }

    [Fact]
    public void ParsesAddItem()
    {
        var line = "[18:17:57] LocalPlayer: ProcessAddItem(AppleJuice(98931165), -1, True)";
        var evt = _parser.TryParse(line, DateTime.UtcNow);

        evt.Should().BeOfType<ItemAdded>();
        var added = (ItemAdded)evt!;
        added.InternalName.Should().Be("AppleJuice");
        added.InstanceId.Should().Be(98931165);
    }

    [Fact]
    public void ParsesDeleteItem()
    {
        var line = "[18:17:59] LocalPlayer: ProcessDeleteItem(98931165)";
        var evt = _parser.TryParse(line, DateTime.UtcNow);

        evt.Should().BeOfType<ItemDeleted>();
        var deleted = (ItemDeleted)evt!;
        deleted.InstanceId.Should().Be(98931165);
    }

    [Fact]
    public void ReturnsNull_ForUnrelatedLine()
    {
        var line = "LocalPlayer: ProcessSomeOtherThing(abc)";
        _parser.TryParse(line, DateTime.UtcNow).Should().BeNull();
    }

    [Fact]
    public void ReturnsNull_ForEmptyLine()
    {
        _parser.TryParse("", DateTime.UtcNow).Should().BeNull();
    }
}
