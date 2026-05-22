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
    public void ReturnsNull_ForDeleteItem_PostMigration()
    {
        // Post-#608 the parser no longer consumes ProcessDeleteItem — the
        // gift-detection FSM is lifted into
        // Mithril.GameState.Gifting.IGiftSignalService (Tier-2 signal service),
        // which owns its own L1 subscription and parses this verb there.
        // The parser must skip these lines without emitting any event.
        var line = "[18:17:59] LocalPlayer: ProcessDeleteItem(98931165)";
        _parser.TryParse(line, DateTime.UtcNow).Should().BeNull();
    }

    [Fact]
    public void ReturnsNull_ForDeltaFavor_PostMigration()
    {
        // Post-#691 the parser no longer consumes ProcessDeltaFavor — the
        // gift-detection FSM owns the verb-triple correlation on its own L1
        // pump inside IGiftSignalService. The parser must skip these lines
        // without emitting any event.
        var line = "[18:14:21] LocalPlayer: ProcessDeltaFavor(0, \"NPC_Fainor\", 23, True)";
        _parser.TryParse(line, DateTime.UtcNow).Should().BeNull();
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
