using FluentAssertions;
using Mithril.GameState.Weather;
using Xunit;

namespace Mithril.GameState.Tests.Weather;

public sealed class WeatherLogParserTests
{
    private readonly WeatherLogParser _parser = new();
    private static readonly DateTime Ts = new(2026, 5, 18, 19, 50, 42, DateTimeKind.Utc);

    // The single real captured sample (2026-05-18).
    private const string FoggyLine =
        "[19:50:42] LocalPlayer: ProcessSetWeather(\"Foggy\", True)";

    [Fact]
    public void Captured_sample_parses_condition_flag_and_timestamp()
    {
        var evt = _parser.TryParse(FoggyLine, Ts)
            .Should().BeOfType<WeatherChangedEvent>().Subject;

        evt.Timestamp.Should().Be(Ts);
        evt.Condition.Should().Be("Foggy");
        evt.Flag.Should().BeTrue();
    }

    [Fact]
    public void False_flag_form_parses()
    {
        var evt = _parser.TryParse(
                "[19:50:42] LocalPlayer: ProcessSetWeather(\"Clear\", False)", Ts)
            .Should().BeOfType<WeatherChangedEvent>().Subject;

        evt.Condition.Should().Be("Clear");
        evt.Flag.Should().BeFalse();
    }

    [Theory]
    [InlineData("Rainy")]
    [InlineData("Snowy")]
    [InlineData("Overcast")]
    [InlineData("")] // tolerate an empty condition rather than throw
    public void Condition_string_is_surfaced_verbatim(string condition)
    {
        var line = $"[19:50:42] LocalPlayer: ProcessSetWeather(\"{condition}\", True)";
        _parser.TryParse(line, Ts)
            .Should().BeOfType<WeatherChangedEvent>()
            .Which.Condition.Should().Be(condition);
    }

    [Fact]
    public void Tolerates_whitespace_variation_inside_the_arg_list()
    {
        var evt = _parser.TryParse(
                "[19:50:42] LocalPlayer: ProcessSetWeather( \"Foggy\" ,  False )", Ts)
            .Should().BeOfType<WeatherChangedEvent>().Subject;

        evt.Condition.Should().Be("Foggy");
        evt.Flag.Should().BeFalse();
    }

    [Theory]
    [InlineData("[19:50:42] LocalPlayer: ProcessNewPosition((1.0, 2.0, 3.0), 0)")]
    [InlineData("[19:50:42] LocalPlayer: ProcessMapPinAdd(1, 0, 0, (1.0, 0.00, 2.0), \"x\")")]
    [InlineData("Loading preferences from C:/Users/x/GorgonSettings.txt")]
    [InlineData("")]
    public void Unrelated_or_blank_lines_return_null(string line)
        => _parser.TryParse(line, Ts).Should().BeNull();

    [Theory]
    [InlineData("[19:50:42] LocalPlayer: ProcessSetWeather(\"Foggy\")")]            // missing flag
    [InlineData("[19:50:42] LocalPlayer: ProcessSetWeather(\"Foggy\", Maybe)")]     // non-bool flag
    [InlineData("[19:50:42] LocalPlayer: ProcessSetWeather(Foggy, True)")]          // unquoted condition
    public void Malformed_weather_line_returns_null_without_throwing(string line)
    {
        var act = () => _parser.TryParse(line, Ts);
        act.Should().NotThrow();
        act().Should().BeNull();
    }
}
