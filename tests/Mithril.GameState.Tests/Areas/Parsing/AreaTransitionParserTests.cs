using FluentAssertions;
using Mithril.GameState.Areas.Parsing;
using Xunit;

namespace Mithril.GameState.Tests.Areas.Parsing;

public sealed class AreaTransitionParserTests
{
    private readonly AreaTransitionParser _parser = new();

    [Theory]
    [InlineData("LOADING LEVEL AreaSerbule", "AreaSerbule")]
    [InlineData("LOADING LEVEL AreaEltibule", "AreaEltibule")]
    [InlineData("LOADING LEVEL AreaTomb1", "AreaTomb1")]
    [InlineData("LOADING LEVEL AreaCave1", "AreaCave1")]
    [InlineData("LOADING LEVEL AreaCasino", "AreaCasino")]
    // PlayerLogTailReader emits raw lines with the [HH:MM:SS] prefix intact —
    // it parses the prefix for sequencing but does not strip it. #199.
    [InlineData("[10:35:44] LOADING LEVEL AreaSerbule", "AreaSerbule")]
    [InlineData("[17:28:06] LOADING LEVEL AreaEltibule", "AreaEltibule")]
    public void Parses_real_area_keys(string line, string expectedArea)
    {
        var evt = (AreaTransitionEvent?)_parser.TryParse(line, DateTime.UtcNow);
        evt.Should().NotBeNull();
        evt!.AreaKey.Should().Be(expectedArea);
    }

    [Theory]
    [InlineData("LOADING LEVEL ChooseCharacter")]
    [InlineData("[19:13:54] LOADING LEVEL ChooseCharacter")]
    public void Parses_ChooseCharacter_as_null_area(string line)
    {
        var evt = (AreaTransitionEvent?)_parser.TryParse(line, DateTime.UtcNow);
        evt.Should().NotBeNull();
        evt!.AreaKey.Should().BeNull();
    }

    [Theory]
    [InlineData("LOADING LEVEL ")]
    [InlineData("[18:30:35] LOADING LEVEL ")]
    public void Parses_empty_body_as_null_area(string line)
    {
        // Disconnect emits "LOADING LEVEL " with empty body — captured 2026-05-09.
        var evt = (AreaTransitionEvent?)_parser.TryParse(line, DateTime.UtcNow);
        evt.Should().NotBeNull();
        evt!.AreaKey.Should().BeNull();
    }

    [Theory]
    [InlineData("LOADING LEVEL StartupMenu")]
    [InlineData("[04:50:05] LOADING LEVEL ReconnectToServer")]
    public void Parses_unknown_non_area_level_as_null(string line)
    {
        // Defensive — anything that doesn't start with "Area" isn't a real
        // game area for our purposes (e.g. menu / loading / reconnect screens).
        var evt = (AreaTransitionEvent?)_parser.TryParse(line, DateTime.UtcNow);
        evt.Should().NotBeNull();
        evt!.AreaKey.Should().BeNull();
    }

    [Fact]
    public void Parses_with_leading_whitespace()
    {
        // Defensive against any leading whitespace, e.g. if PG ever indents
        // the line or a future reader change normalises the prefix.
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
