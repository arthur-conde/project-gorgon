using FluentAssertions;
using Gandalf.Parsing;
using Xunit;

namespace Gandalf.Tests.Parsing;

public sealed class InteractionDelayLoopParserTests
{
    private readonly InteractionDelayLoopParser _parser = new();

    [Fact]
    public void Parses_harvest_with_interactor_flag()
    {
        // LemonTree fruit harvest — IsInteractorDelayLoop is the discriminator
        // that tells the bracket tracker to suppress the chest commit.
        var line = "[18:33:03] LocalPlayer: ProcessDoDelayLoop(3, Gather, \"Collecting Fruit...\", 0, AbortIfAttacked, IsInteractorDelayLoop)";
        var evt = (InteractionDelayLoopEvent?)_parser.TryParse(line, DateTime.UtcNow);

        evt.Should().NotBeNull();
        evt!.Verb.Should().Be("Gather");
        evt.IsInteractor.Should().BeTrue();
    }

    [Theory]
    [InlineData("LocalPlayer: ProcessDoDelayLoop(1.5, Eat, \"Using Ranalon Salad\", 5820, AbortIfAttacked)", "Eat")]
    [InlineData("LocalPlayer: ProcessDoDelayLoop(1.5, Drink, \"Using Grape Juice\", 5313, AbortIfAttacked)", "Drink")]
    [InlineData("LocalPlayer: ProcessDoDelayLoop(3, UseItem, \"Distilling Phlogiston\", 5815, AbortIfAttacked)", "UseItem")]
    [InlineData("LocalPlayer: ProcessDoDelayLoop(10, UseTeleportationCircle, \"Recalling Other Places\", 3713, AbortIfAttacked)", "UseTeleportationCircle")]
    public void Parses_self_targeted_loops_without_interactor_flag(string line, string expectedVerb)
    {
        var evt = (InteractionDelayLoopEvent?)_parser.TryParse(line, DateTime.UtcNow);
        evt.Should().NotBeNull();
        evt!.Verb.Should().Be(expectedVerb);
        evt.IsInteractor.Should().BeFalse();
    }

    [Fact]
    public void Captures_timestamp_passed_in()
    {
        var ts = new DateTime(2026, 5, 1, 18, 33, 3, DateTimeKind.Utc);
        var line = "LocalPlayer: ProcessDoDelayLoop(3, Gather, \"Collecting Fruit...\", 0, AbortIfAttacked, IsInteractorDelayLoop)";
        var evt = (InteractionDelayLoopEvent)_parser.TryParse(line, ts)!;
        evt.Timestamp.Should().Be(ts);
    }

    [Fact]
    public void Returns_null_for_unrelated_line() =>
        _parser.TryParse("LocalPlayer: ProcessAddItem(Apple(1), -1, True)", DateTime.UtcNow).Should().BeNull();

    [Fact]
    public void Returns_null_for_empty_line() =>
        _parser.TryParse("", DateTime.UtcNow).Should().BeNull();
}
