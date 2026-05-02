using FluentAssertions;
using Gandalf.Parsing;
using Xunit;

namespace Gandalf.Tests.Parsing;

public sealed class InteractionEndParserTests
{
    private readonly InteractionEndParser _parser = new();

    [Fact]
    public void Parses_portal_end_sample()
    {
        // From live capture (#91): portal closes via ProcessEndInteraction(id).
        var line = "[17:27:57] LocalPlayer: ProcessEndInteraction(-158)";
        var evt = (InteractionEndEvent?)_parser.TryParse(line, DateTime.UtcNow);

        evt.Should().NotBeNull();
        evt!.InteractorId.Should().Be(-158);
    }

    [Fact]
    public void Parses_positive_id()
    {
        var line = "LocalPlayer: ProcessEndInteraction(9902924)";
        var evt = (InteractionEndEvent?)_parser.TryParse(line, DateTime.UtcNow);

        evt.Should().NotBeNull();
        evt!.InteractorId.Should().Be(9902924);
    }

    [Fact]
    public void Captures_timestamp_passed_in()
    {
        var ts = new DateTime(2026, 5, 1, 18, 33, 6, DateTimeKind.Utc);
        var evt = (InteractionEndEvent)_parser.TryParse("LocalPlayer: ProcessEndInteraction(9902924)", ts)!;
        evt.Timestamp.Should().Be(ts);
    }

    [Fact]
    public void Returns_null_for_start_interaction() =>
        _parser.TryParse(
            "LocalPlayer: ProcessStartInteraction(-158, 8, 0, False, \"Portal\")",
            DateTime.UtcNow).Should().BeNull();

    [Fact]
    public void Returns_null_for_unrelated_line() =>
        _parser.TryParse("LocalPlayer: ProcessAddItem(Apple(1), -1, True)", DateTime.UtcNow).Should().BeNull();

    [Fact]
    public void Returns_null_for_empty_line() =>
        _parser.TryParse("", DateTime.UtcNow).Should().BeNull();
}
