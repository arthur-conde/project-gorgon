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
    // Real captures from Player-prev.log (2026-04-29 / 2026-04-30, lines 42344,
    // 488775, 893395). Three distinct quest IDs verified via the mithril-logs MCP.
    private readonly QuestCompletedParser _parser;
    private readonly FakeReferenceData _refData;

    public QuestCompletedParserTests()
    {
        _refData = new FakeReferenceData([
            QuestEntryFactory.Repeatable("quest_14003", "Quest_Sample_14003", "Sample 14003", TimeSpan.FromHours(20)),
            QuestEntryFactory.Repeatable("quest_20803", "Quest_Sample_20803", "Sample 20803", TimeSpan.FromHours(20)),
            QuestEntryFactory.Repeatable("quest_25010", "Quest_Sample_25010", "Sample 25010", TimeSpan.FromHours(20)),
        ]);
        _parser = new QuestCompletedParser(_refData);
    }

    [Fact]
    public void Parses_real_capture_quest_14003()
    {
        var line = "[15:34:16] LocalPlayer: ProcessCompleteQuest(8298169, 14003)";
        var evt = _parser.TryParse(line, DateTime.UtcNow);

        evt.Should().BeOfType<QuestCompletedEvent>();
        ((QuestCompletedEvent)evt!).QuestInternalName.Should().Be("Quest_Sample_14003");
    }

    [Fact]
    public void Parses_real_capture_quest_20803()
    {
        var line = "[01:44:49] LocalPlayer: ProcessCompleteQuest(8705565, 20803)";
        var evt = (QuestCompletedEvent?)_parser.TryParse(line, DateTime.UtcNow);

        evt.Should().NotBeNull();
        evt!.QuestInternalName.Should().Be("Quest_Sample_20803");
    }

    [Fact]
    public void Parses_real_capture_quest_25010()
    {
        var line = "[04:15:38] LocalPlayer: ProcessCompleteQuest(8819335, 25010)";
        var evt = (QuestCompletedEvent?)_parser.TryParse(line, DateTime.UtcNow);

        evt.Should().NotBeNull();
        evt!.QuestInternalName.Should().Be("Quest_Sample_25010");
    }

    [Fact]
    public void Captures_timestamp_passed_in()
    {
        var ts = new DateTime(2026, 4, 30, 12, 34, 56, DateTimeKind.Utc);
        var evt = (QuestCompletedEvent?)_parser.TryParse(
            "LocalPlayer: ProcessCompleteQuest(8298169, 14003)", ts);

        evt.Should().NotBeNull();
        evt!.Timestamp.Should().Be(ts);
    }

    [Fact]
    public void Returns_null_for_unknown_quest_id()
    {
        // Game-data drift: a line with a questId not present in the reference
        // data is dropped silently rather than throwing.
        var line = "LocalPlayer: ProcessCompleteQuest(1, 999999)";
        _parser.TryParse(line, DateTime.UtcNow).Should().BeNull();
    }

    [Fact]
    public void Returns_null_for_unrelated_line() =>
        _parser.TryParse("LocalPlayer: ProcessAddItem(Apple(1234), -1, True)", DateTime.UtcNow).Should().BeNull();

    [Fact]
    public void Returns_null_for_empty_line() =>
        _parser.TryParse("", DateTime.UtcNow).Should().BeNull();

    [Fact]
    public void Returns_null_for_old_quoted_string_shape() =>
        // Defensive — confirms the rewritten regex no longer matches the
        // pre-#77 placeholder shape (would hide a regression in the lookup).
        _parser.TryParse("LocalPlayer: ProcessCompleteQuest(\"Q1\")", DateTime.UtcNow).Should().BeNull();
}
