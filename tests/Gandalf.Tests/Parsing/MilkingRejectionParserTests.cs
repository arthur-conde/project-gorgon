using FluentAssertions;
using Gandalf.Parsing;
using Xunit;

namespace Gandalf.Tests.Parsing;

public sealed class MilkingRejectionParserTests
{
    private readonly MilkingRejectionParser _parser = new();

    [Fact]
    public void Parses_wiki_sample_singular_hour_rejection()
    {
        // From wiki: Player-Log-Signals § Wild gathering cooldowns (cows, trees) § Cow rejection.
        var line = "[23:39:45] LocalPlayer: ProcessScreenText(ErrorMessage, \"You've already milked Bessie in the past hour.\")";
        var evt = _parser.TryParse(line, DateTime.UtcNow);

        evt.Should().BeOfType<ChestCooldownObservedEvent>();
        ((ChestCooldownObservedEvent)evt!).Duration.Should().Be(TimeSpan.FromHours(1));
    }

    [Theory]
    [InlineData("in the past hour", 60)]
    [InlineData("in the past day", 1440)]
    [InlineData("in the past minute", 1)]
    public void Parses_singular_unit_forms(string fragment, int expectedTotalMinutes)
    {
        var line = $"ProcessScreenText(ErrorMessage, \"You've already milked Bessie {fragment}.\")";
        var evt = (ChestCooldownObservedEvent?)_parser.TryParse(line, DateTime.UtcNow);

        evt.Should().NotBeNull();
        evt!.Duration.TotalMinutes.Should().Be(expectedTotalMinutes);
    }

    [Theory]
    [InlineData("in the past 30 minutes", 30)]
    [InlineData("in the past 5 minutes", 5)]
    [InlineData("in the past 2 hours", 120)]
    [InlineData("in the past 90 minutes", 90)]
    public void Parses_numeric_unit_forms(string fragment, int expectedTotalMinutes)
    {
        var line = $"ProcessScreenText(ErrorMessage, \"You've already milked Bessie {fragment}.\")";
        var evt = (ChestCooldownObservedEvent?)_parser.TryParse(line, DateTime.UtcNow);

        evt.Should().NotBeNull();
        evt!.Duration.TotalMinutes.Should().Be(expectedTotalMinutes);
    }

    [Fact]
    public void Cow_friendly_name_does_not_affect_parsing()
    {
        // The parser doesn't extract the cow name (the bracket tracker has the
        // internal name); but the regex must accept any single-token name.
        var lines = new[]
        {
            "ProcessScreenText(ErrorMessage, \"You've already milked Bessie in the past hour.\")",
            "ProcessScreenText(ErrorMessage, \"You've already milked Moolanda in the past hour.\")",
            "ProcessScreenText(ErrorMessage, \"You've already milked Daisy-May in the past hour.\")",
        };
        foreach (var line in lines)
        {
            var evt = _parser.TryParse(line, DateTime.UtcNow);
            evt.Should().NotBeNull(line);
            ((ChestCooldownObservedEvent)evt!).Duration.Should().Be(TimeSpan.FromHours(1));
        }
    }

    [Fact]
    public void Returns_null_for_chest_rejection_grammar()
    {
        // Chest rejection is on a different channel (GeneralInfo) and uses
        // a different grammar — must not match.
        var line = "ProcessScreenText(GeneralInfo, \"You've already looted this chest! (It will refill 3 hours after you looted it.)\")";
        _parser.TryParse(line, DateTime.UtcNow).Should().BeNull();
    }

    [Fact]
    public void Returns_null_for_unrelated_error_message()
    {
        // A different ErrorMessage rejection that happens to share the channel.
        var line = "ProcessScreenText(ErrorMessage, \"You can't do that while your inventory is overflowing. You're too encumbered!\")";
        _parser.TryParse(line, DateTime.UtcNow).Should().BeNull();
    }

    [Fact]
    public void Returns_null_for_empty_line() =>
        _parser.TryParse("", DateTime.UtcNow).Should().BeNull();
}
