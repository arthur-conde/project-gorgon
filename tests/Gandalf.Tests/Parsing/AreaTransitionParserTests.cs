using FluentAssertions;
using Gandalf.Parsing;
using Xunit;

namespace Gandalf.Tests.Parsing;

public sealed class AreaTransitionParserTests
{
    private readonly AreaTransitionParser _parser = new();

    [Theory]
    [InlineData("LOADING LEVEL AreaSerbule", "AreaSerbule")]
    [InlineData("LOADING LEVEL AreaEltibule", "AreaEltibule")]
    [InlineData("LOADING LEVEL AreaTomb1", "AreaTomb1")]
    [InlineData("LOADING LEVEL AreaCave1", "AreaCave1")]
    [InlineData("LOADING LEVEL AreaCasino", "AreaCasino")]
    public void Parses_real_area_keys(string line, string expectedArea)
    {
        var evt = (AreaTransitionEvent?)_parser.TryParse(line, DateTime.UtcNow);
        evt.Should().NotBeNull();
        evt!.AreaKey.Should().Be(expectedArea);
    }

    [Fact]
    public void Parses_ChooseCharacter_as_null_area()
    {
        var evt = (AreaTransitionEvent?)_parser.TryParse("LOADING LEVEL ChooseCharacter", DateTime.UtcNow);
        evt.Should().NotBeNull();
        evt!.AreaKey.Should().BeNull();
    }

    [Fact]
    public void Parses_empty_body_as_null_area()
    {
        // Disconnect emits "LOADING LEVEL " with empty body — captured 2026-05-09.
        var evt = (AreaTransitionEvent?)_parser.TryParse("LOADING LEVEL ", DateTime.UtcNow);
        evt.Should().NotBeNull();
        evt!.AreaKey.Should().BeNull();
    }

    [Fact]
    public void Parses_unknown_non_area_level_as_null()
    {
        // Defensive — anything that doesn't start with "Area" isn't a real
        // game area for our purposes (e.g. menu / loading screens).
        var evt = (AreaTransitionEvent?)_parser.TryParse("LOADING LEVEL StartupMenu", DateTime.UtcNow);
        evt.Should().NotBeNull();
        evt!.AreaKey.Should().BeNull();
    }

    [Fact]
    public void Parses_with_leading_whitespace()
    {
        // PlayerLogTailReader strips the [HH:MM:SS] prefix; defensive against
        // any leading whitespace that might survive.
        var evt = (AreaTransitionEvent?)_parser.TryParse("  LOADING LEVEL AreaSerbule", DateTime.UtcNow);
        evt.Should().NotBeNull();
        evt!.AreaKey.Should().Be("AreaSerbule");
    }

    [Fact]
    public void Returns_null_for_unrelated_line()
    {
        _parser.TryParse("LocalPlayer: ProcessAddItem(Apple(123), -1, True)", DateTime.UtcNow)
            .Should().BeNull();
    }

    [Fact]
    public void Returns_null_for_loading_text_inside_quoted_payload()
    {
        // "LOADING LEVEL" appearing as substring inside an unrelated message
        // body — regex anchors ensure we only match a true area-load line.
        _parser.TryParse("ProcessChat(General, \"It says LOADING LEVEL on the screen\")", DateTime.UtcNow)
            .Should().BeNull();
    }

    [Fact]
    public void Returns_null_for_empty_line() =>
        _parser.TryParse("", DateTime.UtcNow).Should().BeNull();

    [Fact]
    public void Preserves_event_timestamp()
    {
        var ts = new DateTime(2026, 5, 10, 17, 11, 10, DateTimeKind.Utc);
        var evt = (AreaTransitionEvent?)_parser.TryParse("LOADING LEVEL AreaSerbule", ts);
        evt!.Timestamp.Should().Be(ts);
    }
}
