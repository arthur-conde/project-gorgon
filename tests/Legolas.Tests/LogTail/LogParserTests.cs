using FluentAssertions;
using Legolas.Domain;
using Legolas.Services;

namespace Legolas.Tests.LogTail;

public class LogParserTests
{
    private static readonly DateTime FixedTime = new(2026, 4, 14, 12, 0, 0, DateTimeKind.Utc);
    private readonly ChatLogParser _parser = new();

    [Theory]
    [InlineData("[Status] The Bloodstone is 528m west and 202m north.", "Bloodstone", -528, 202)]
    [InlineData("[Status] The Diamond is 20m east and 14m south.", "Diamond", 20, -14)]
    [InlineData("[Status] The Star Sapphire is 137m north and 88m west.", "Star Sapphire", -88, 137)]
    [InlineData("[Status] The Lump of Coal is 9m east and 0m north.", "Lump of Coal", 9, 0)]
    public void Parses_survey_detected(string line, string expectedName, int expectedEast, int expectedNorth)
    {
        var evt = _parser.TryParse(line, FixedTime);

        var survey = evt.Should().BeOfType<SurveyDetected>().Subject;
        survey.Name.Should().Be(expectedName);
        survey.Offset.East.Should().Be(expectedEast);
        survey.Offset.North.Should().Be(expectedNorth);
    }

    [Theory]
    [InlineData("[Status] Diamond x1 collected!", "Diamond", 1)]
    [InlineData("[Status] Gold Ingot x5 collected!", "Gold Ingot", 5)]
    public void Parses_item_collected(string line, string expectedName, int expectedCount)
    {
        var evt = _parser.TryParse(line, FixedTime);

        evt.Should().BeOfType<ItemCollected>()
            .Which.Should().BeEquivalentTo(new ItemCollected(FixedTime, expectedName, expectedCount));
    }

    [Fact]
    public void Parses_motherlode_distance()
    {
        var evt = _parser.TryParse("The treasure is 43 metres from here", FixedTime);

        evt.Should().BeOfType<MotherlodeDistance>()
            .Which.DistanceMetres.Should().Be(43);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void Returns_null_for_blank(string line)
    {
        var evt = _parser.TryParse(line, FixedTime);

        evt.Should().BeNull();
    }

    [Theory]
    [InlineData("random unrelated chatter")]
    [InlineData("[Status] something else entirely")]
    public void Returns_unknown_for_unrecognised(string line)
    {
        var evt = _parser.TryParse(line, FixedTime);

        evt.Should().BeOfType<UnknownLine>().Which.RawLine.Should().Be(line);
    }

    [Fact]
    public void Survey_handles_swapped_axis_order()
    {
        // The game can list either component first; parser must compose by direction
        // word, not position.
        var evt = _parser.TryParse("[Status] The Foo is 5m north and 12m east.", FixedTime);

        var survey = evt.Should().BeOfType<SurveyDetected>().Subject;
        survey.Offset.East.Should().Be(12);
        survey.Offset.North.Should().Be(5);
    }
}
