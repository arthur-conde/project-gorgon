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

        evt.Should().BeOfType<InteractionStartEvent>();
        var start = (InteractionStartEvent)evt!;
        start.EntityName.Should().Be("GoblinStaticChest1");
        start.InteractorId.Should().Be(-162);
    }

    [Fact]
    public void Parses_EltibuleSecretChest_sample()
    {
        // Live capture: this loot chest was missed by the v1 substring filter
        // (Contains("StaticChest")) — the bug #73 fixes by moving filtering
        // from the parser's name heuristic to the bracket state machine.
        var line = "[03:41:37] LocalPlayer: ProcessStartInteraction(-147, 5, 0, False, \"EltibuleSecretChest\")";
        var start = (InteractionStartEvent?)_parser.TryParse(line, DateTime.UtcNow);

        start.Should().NotBeNull();
        start!.EntityName.Should().Be("EltibuleSecretChest");
        start.InteractorId.Should().Be(-147);
    }

    [Fact]
    public void Emits_event_for_non_chest_interaction()
    {
        // After #73, the parser is intentionally broad — discriminates loot
        // from storage/NPC happens downstream in LootBracketTracker. The
        // parser now emits for *all* ProcessStartInteraction lines.
        var line = "LocalPlayer: ProcessStartInteraction(31190, 7, 0, False, \"SerbuleCommunityChest\")";
        var start = (InteractionStartEvent?)_parser.TryParse(line, DateTime.UtcNow);

        start.Should().NotBeNull();
        start!.EntityName.Should().Be("SerbuleCommunityChest");
        start.InteractorId.Should().Be(31190);
    }

    [Fact]
    public void Captures_timestamp_passed_in()
    {
        var ts = new DateTime(2026, 4, 30, 18, 22, 2, DateTimeKind.Utc);
        var line = "LocalPlayer: ProcessStartInteraction(-162, 7, 0, False, \"GoblinStaticChest1\")";
        var evt = (InteractionStartEvent)_parser.TryParse(line, ts)!;

        evt.Timestamp.Should().Be(ts);
    }

    [Fact]
    public void Returns_null_for_unrelated_line() =>
        _parser.TryParse("LocalPlayer: ProcessAddItem(Apple(1234), -1, True)", DateTime.UtcNow).Should().BeNull();

    [Fact]
    public void Returns_null_for_empty_line() =>
        _parser.TryParse("", DateTime.UtcNow).Should().BeNull();
}
