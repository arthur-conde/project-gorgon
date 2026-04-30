using FluentAssertions;
using Gandalf.Parsing;
using Xunit;

namespace Gandalf.Tests.Parsing;

public sealed class QuestLoadedParserTests
{
    private readonly QuestLoadedParser _parser = new();

    [Fact]
    public void Parses_load_quest_line()
    {
        // Verification owed: exact shape pending #60 spike. v1 expects the
        // first quoted argument to carry the InternalName.
        var line = "[12:34:56] LocalPlayer: ProcessLoadQuest(\"Quest_RepeatableSerbule01\", 0, True)";
        var evt = _parser.TryParse(line, DateTime.UtcNow);

        evt.Should().BeOfType<QuestLoadedEvent>();
        ((QuestLoadedEvent)evt!).QuestInternalName.Should().Be("Quest_RepeatableSerbule01");
    }

    [Fact]
    public void Returns_null_for_unrelated_line() =>
        _parser.TryParse("LocalPlayer: ProcessAddItem(Apple(1234), -1, True)", DateTime.UtcNow).Should().BeNull();

    [Fact]
    public void Returns_null_for_empty_line() =>
        _parser.TryParse("", DateTime.UtcNow).Should().BeNull();
}

public sealed class QuestCompletedParserTests
{
    private readonly QuestCompletedParser _parser = new();

    [Fact]
    public void Parses_complete_quest_line()
    {
        var line = "[12:34:56] LocalPlayer: ProcessCompleteQuest(\"Quest_RepeatableSerbule01\", True)";
        var evt = _parser.TryParse(line, DateTime.UtcNow);

        evt.Should().BeOfType<QuestCompletedEvent>();
        ((QuestCompletedEvent)evt!).QuestInternalName.Should().Be("Quest_RepeatableSerbule01");
    }

    [Fact]
    public void Captures_timestamp_passed_in()
    {
        var ts = new DateTime(2026, 4, 30, 12, 34, 56, DateTimeKind.Utc);
        var evt = (QuestCompletedEvent?)_parser.TryParse(
            "LocalPlayer: ProcessCompleteQuest(\"Q1\")", ts);

        evt.Should().NotBeNull();
        evt!.Timestamp.Should().Be(ts);
    }

    [Fact]
    public void Returns_null_for_load_line() =>
        _parser.TryParse("LocalPlayer: ProcessLoadQuest(\"Q1\")", DateTime.UtcNow).Should().BeNull();

    [Fact]
    public void Returns_null_for_empty_line() =>
        _parser.TryParse("", DateTime.UtcNow).Should().BeNull();
}
