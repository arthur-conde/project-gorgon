using FluentAssertions;
using Gandalf.Parsing;
using Xunit;

namespace Gandalf.Tests.Parsing;

public sealed class ChestInteractionParserTests
{
    private readonly ChestInteractionParser _parser = new();

    [Fact]
    public void Parses_wiki_sample_GoblinStaticChest1()
    {
        // From wiki: Player-Log-Signals § Static treasure chests § Interaction sequence.
        var line = "[18:22:02] LocalPlayer: ProcessStartInteraction(-162, 7, 0, False, \"GoblinStaticChest1\")";
        var evt = _parser.TryParse(line, DateTime.UtcNow);

        evt.Should().BeOfType<ChestInteractionEvent>();
        ((ChestInteractionEvent)evt!).ChestInternalName.Should().Be("GoblinStaticChest1");
    }

    [Fact]
    public void Captures_timestamp_passed_in()
    {
        var ts = new DateTime(2026, 4, 30, 18, 22, 2, DateTimeKind.Utc);
        var line = "LocalPlayer: ProcessStartInteraction(-162, 7, 0, False, \"GoblinStaticChest1\")";
        var evt = (ChestInteractionEvent)_parser.TryParse(line, ts)!;

        evt.Timestamp.Should().Be(ts);
    }

    [Fact]
    public void Returns_null_for_non_chest_interaction()
    {
        // NPC vendor interaction shape — same line family, different name (no "StaticChest").
        var line = "LocalPlayer: ProcessStartInteraction(42, 0, 0, False, \"NpcMaxine\")";
        _parser.TryParse(line, DateTime.UtcNow).Should().BeNull();
    }

    [Fact]
    public void Returns_null_for_unrelated_line()
    {
        _parser.TryParse("LocalPlayer: ProcessAddItem(Apple(1234), -1, True)", DateTime.UtcNow).Should().BeNull();
    }

    [Fact]
    public void Returns_null_for_empty_line() =>
        _parser.TryParse("", DateTime.UtcNow).Should().BeNull();
}
