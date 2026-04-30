using FluentAssertions;
using Gandalf.Parsing;
using Xunit;

namespace Gandalf.Tests.Parsing;

public sealed class ChestRejectionParserTests
{
    private readonly ChestRejectionParser _parser = new();

    [Fact]
    public void Parses_wiki_sample_3_hour_rejection()
    {
        // From wiki: Player-Log-Signals § Static treasure chests § Refill cooldown signal.
        var line = "[01:31:58] LocalPlayer: ProcessScreenText(GeneralInfo, \"You've already looted this chest! (It will refill 3 hours after you looted it.)\")";
        var evt = _parser.TryParse(line, DateTime.UtcNow);

        evt.Should().BeOfType<ChestCooldownObservedEvent>();
        ((ChestCooldownObservedEvent)evt!).Duration.Should().Be(TimeSpan.FromHours(3));
    }

    [Theory]
    [InlineData("It will refill 1 hour after", 60)]
    [InlineData("It will refill 30 minutes after", 30)]
    [InlineData("It will refill 12 hours after", 720)]
    [InlineData("It will refill 1 day after", 1440)]
    public void Parses_alternate_unit_phrasings(string fragment, int expectedTotalMinutes)
    {
        var line = $"ProcessScreenText(GeneralInfo, \"You've already looted this chest! ({fragment} you looted it.)\")";
        var evt = (ChestCooldownObservedEvent?)_parser.TryParse(line, DateTime.UtcNow);

        evt.Should().NotBeNull();
        evt!.Duration.TotalMinutes.Should().Be(expectedTotalMinutes);
    }

    [Fact]
    public void Returns_null_for_unrelated_screen_text()
    {
        var line = "ProcessScreenText(GeneralInfo, \"You earned some XP\")";
        _parser.TryParse(line, DateTime.UtcNow).Should().BeNull();
    }

    [Fact]
    public void Returns_null_for_empty_line() =>
        _parser.TryParse("", DateTime.UtcNow).Should().BeNull();
}
