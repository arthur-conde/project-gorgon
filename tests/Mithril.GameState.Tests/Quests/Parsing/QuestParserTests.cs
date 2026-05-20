using FluentAssertions;
using Mithril.GameState.Quests.Parsing;
using Mithril.TestSupport;
using Xunit;

namespace Mithril.GameState.Tests.Quests.Parsing;

public sealed class QuestJournalLoadParserTests
{
    private readonly QuestJournalLoadParser _parser = new();

    [Fact]
    public void Parses_real_login_capture_with_two_lists()
    {
        // From Player-prev.log:2173, captured 2026-04-29 15:12:44. Trimmed for
        // readability — full capture has ~18 work-order ids and ~280 regular ids.
        // Envelope-stripped per #550 L1 (L0.5 eats `[ts] LocalPlayer: `).
        var line = "ProcessLoadQuests(8285856, TransitionalQuestState[], "
                 + "[50208,51252,51258,50675,], [3,4,5,21001,21501,])";
        var evt = (QuestJournalLoadedEvent?)_parser.TryParse(line, DateTime.UtcNow);

        evt.Should().NotBeNull();
        evt!.WorkOrderQuestIds.Should().Equal(50208, 51252, 51258, 50675);
        evt.RegularQuestIds.Should().Equal(3, 4, 5, 21001, 21501);
    }

    [Fact]
    public void Parses_empty_lists()
    {
        var line = "ProcessLoadQuests(123, TransitionalQuestState[], [], [])";
        var evt = (QuestJournalLoadedEvent?)_parser.TryParse(line, DateTime.UtcNow);

        evt.Should().NotBeNull();
        evt!.WorkOrderQuestIds.Should().BeEmpty();
        evt.RegularQuestIds.Should().BeEmpty();
    }

    [Fact]
    public void Returns_null_for_singular_ProcessLoadQuest() =>
        // Defensive — the placeholder-era regex matched a (non-existent)
        // singular ProcessLoadQuest line. Confirm the new parser ignores it.
        _parser.TryParse("ProcessLoadQuest(\"Quest_X\", 0, True)", DateTime.UtcNow).Should().BeNull();

    [Fact]
    public void Returns_null_for_unrelated_line() =>
        _parser.TryParse("ProcessAddItem(Apple(1234), -1, True)", DateTime.UtcNow).Should().BeNull();

    [Fact]
    public void Returns_null_for_empty_line() =>
        _parser.TryParse("", DateTime.UtcNow).Should().BeNull();
}

public sealed class QuestAcceptedParserTests
{
    // Real captures from Player-prev.log (line 449668) and Player.log (line 65933):
    // ProcessBook("New Quest: <<<quest_25212_Name>>>", ...) and quest_25211.
    private readonly QuestAcceptedParser _parser;

    public QuestAcceptedParserTests()
    {
        var refData = new FakeReferenceData([
            QuestFactory.Repeatable("quest_25211", "Quest_Sample_25211", "Sample 25211", TimeSpan.FromHours(20)),
            QuestFactory.Repeatable("quest_25212", "Quest_Sample_25212", "Sample 25212", TimeSpan.FromHours(20)),
        ]);
        _parser = new QuestAcceptedParser(refData);
    }

    [Fact]
    public void Parses_real_capture_quest_25212()
    {
        // Envelope-stripped per #550 L1.
        var line = "ProcessBook(\"New Quest: <<<quest_25212_Name>>>\", "
                 + "\"<<<quest_25212_Preface>>>\", \"\", \"\", \"\", False, False, False, False, False, \"\")";
        var evt = (QuestAcceptedEvent?)_parser.TryParse(line, DateTime.UtcNow);

        evt.Should().NotBeNull();
        evt!.QuestInternalName.Should().Be("Quest_Sample_25212");
    }

    [Fact]
    public void Parses_real_capture_quest_25211()
    {
        var line = "ProcessBook(\"New Quest: <<<quest_25211_Name>>>\", "
                 + "\"<<<quest_25211_Preface>>>\", \"\", \"\", \"\", False, False, False, False, False, \"\")";
        var evt = (QuestAcceptedEvent?)_parser.TryParse(line, DateTime.UtcNow);

        evt.Should().NotBeNull();
        evt!.QuestInternalName.Should().Be("Quest_Sample_25211");
    }

    [Fact]
    public void Returns_null_for_non_quest_book() =>
        // Other ProcessBook lines (lore books, NPC dialog, etc) never carry
        // the "New Quest:" prefix.
        _parser.TryParse(
            "ProcessBook(\"Whispers from the Void\", \"...\", \"\", \"\", \"\", False, False, False, False, False, \"\")",
            DateTime.UtcNow).Should().BeNull();

    [Fact]
    public void Returns_null_for_unknown_quest_id() =>
        // questId not in reference data — drop silently.
        _parser.TryParse(
            "ProcessBook(\"New Quest: <<<quest_999999_Name>>>\", \"...\", \"\", \"\", \"\", False, False, False, False, False, \"\")",
            DateTime.UtcNow).Should().BeNull();

    [Fact]
    public void Returns_null_for_unrelated_line() =>
        _parser.TryParse("ProcessAddItem(Apple(1234), -1, True)", DateTime.UtcNow).Should().BeNull();

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
            QuestFactory.Repeatable("quest_14003", "Quest_Sample_14003", "Sample 14003", TimeSpan.FromHours(20)),
            QuestFactory.Repeatable("quest_20803", "Quest_Sample_20803", "Sample 20803", TimeSpan.FromHours(20)),
            QuestFactory.Repeatable("quest_25010", "Quest_Sample_25010", "Sample 25010", TimeSpan.FromHours(20)),
        ]);
        _parser = new QuestCompletedParser(_refData);
    }

    [Fact]
    public void Parses_real_capture_quest_14003()
    {
        // Envelope-stripped per #550 L1.
        var line = "ProcessCompleteQuest(8298169, 14003)";
        var evt = _parser.TryParse(line, DateTime.UtcNow);

        evt.Should().BeOfType<QuestCompletedEvent>();
        ((QuestCompletedEvent)evt!).QuestInternalName.Should().Be("Quest_Sample_14003");
    }

    [Fact]
    public void Parses_real_capture_quest_20803()
    {
        var line = "ProcessCompleteQuest(8705565, 20803)";
        var evt = (QuestCompletedEvent?)_parser.TryParse(line, DateTime.UtcNow);

        evt.Should().NotBeNull();
        evt!.QuestInternalName.Should().Be("Quest_Sample_20803");
    }

    [Fact]
    public void Parses_real_capture_quest_25010()
    {
        var line = "ProcessCompleteQuest(8819335, 25010)";
        var evt = (QuestCompletedEvent?)_parser.TryParse(line, DateTime.UtcNow);

        evt.Should().NotBeNull();
        evt!.QuestInternalName.Should().Be("Quest_Sample_25010");
    }

    [Fact]
    public void Captures_timestamp_passed_in()
    {
        var ts = new DateTime(2026, 4, 30, 12, 34, 56, DateTimeKind.Utc);
        var evt = (QuestCompletedEvent?)_parser.TryParse(
            "ProcessCompleteQuest(8298169, 14003)", ts);

        evt.Should().NotBeNull();
        evt!.Timestamp.Should().Be(ts);
    }

    [Fact]
    public void Returns_null_for_unknown_quest_id()
    {
        // Game-data drift: a line with a questId not present in the reference
        // data is dropped silently rather than throwing.
        var line = "ProcessCompleteQuest(1, 999999)";
        _parser.TryParse(line, DateTime.UtcNow).Should().BeNull();
    }

    [Fact]
    public void Returns_null_for_unrelated_line() =>
        _parser.TryParse("ProcessAddItem(Apple(1234), -1, True)", DateTime.UtcNow).Should().BeNull();

    [Fact]
    public void Returns_null_for_empty_line() =>
        _parser.TryParse("", DateTime.UtcNow).Should().BeNull();

    [Fact]
    public void Returns_null_for_old_quoted_string_shape() =>
        // Defensive — confirms the rewritten regex no longer matches the
        // pre-#77 placeholder shape (would hide a regression in the lookup).
        _parser.TryParse("ProcessCompleteQuest(\"Q1\")", DateTime.UtcNow).Should().BeNull();
}
