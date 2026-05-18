using FluentAssertions;
using Mithril.GameState.Celestial;
using Mithril.GameState.Celestial.Parsing;
using Xunit;

namespace Mithril.GameState.Tests.Celestial;

public sealed class CelestialLogParserTests
{
    private static readonly CelestialLogParser Parser = new();
    private static readonly DateTime Ts = new(2026, 5, 18, 19, 50, 42, DateTimeKind.Utc);

    // Byte-exact from a real Player.log (this machine, 2026-05-18).
    private const string RealLine =
        "[19:50:42] LocalPlayer: ProcessSetCelestialInfo(WaxingCrescentMoon)";

    [Fact]
    public void Real_line_parses_to_canonical_phase_and_keeps_raw_token()
    {
        var evt = Parser.TryParse(RealLine, Ts)
            .Should().BeOfType<CelestialInfoEvent>().Subject;

        evt.Phase.Should().Be(MoonPhase.WaxingCrescent);
        evt.RawPhase.Should().Be("WaxingCrescentMoon");
        evt.Timestamp.Should().Be(Ts);
    }

    [Theory]
    // Player.log token spelling (trailing "Moon").
    [InlineData("NewMoon", MoonPhase.NewMoon)]
    [InlineData("WaxingCrescentMoon", MoonPhase.WaxingCrescent)]
    [InlineData("FirstQuarterMoon", MoonPhase.FirstQuarter)]
    [InlineData("WaxingGibbousMoon", MoonPhase.WaxingGibbous)]
    [InlineData("FullMoon", MoonPhase.FullMoon)]
    [InlineData("WaningGibbousMoon", MoonPhase.WaningGibbous)]
    [InlineData("ThirdQuarterMoon", MoonPhase.ThirdQuarter)]
    [InlineData("LastQuarterMoon", MoonPhase.ThirdQuarter)]
    [InlineData("WaningCrescentMoon", MoonPhase.WaningCrescent)]
    // Reference-data discriminator spelling (no suffix) maps the same way.
    [InlineData("WaxingCrescent", MoonPhase.WaxingCrescent)]
    [InlineData("NewMoon ", MoonPhase.NewMoon)]
    public void Maps_all_observed_token_spellings(string token, MoonPhase expected)
    {
        var line = $"[19:50:42] LocalPlayer: ProcessSetCelestialInfo({token})";
        var evt = Parser.TryParse(line, Ts).Should().BeOfType<CelestialInfoEvent>().Subject;

        evt.Phase.Should().Be(expected);
        evt.RawPhase.Should().Be(token.Trim());
    }

    [Fact]
    public void Unrecognised_token_yields_Unknown_but_retains_raw_string()
    {
        var line = "[19:50:42] LocalPlayer: ProcessSetCelestialInfo(BloodMoonEclipse)";
        var evt = Parser.TryParse(line, Ts).Should().BeOfType<CelestialInfoEvent>().Subject;

        evt.Phase.Should().Be(MoonPhase.Unknown);
        evt.RawPhase.Should().Be("BloodMoonEclipse");
        // Display still reads as a phrase rather than a blank.
        evt.Phase.DisplayName(evt.RawPhase).Should().Be("Blood Moon Eclipse");
    }

    [Fact]
    public void Rejects_non_local_actor_token()
    {
        // A bare substring check would be fooled — the boundary lookbehind
        // must reject this (mirrors PlayerPositionParser's discipline).
        Parser.TryParse(
                "[19:50:42] NonLocalPlayer: ProcessSetCelestialInfo(FullMoon)", Ts)
            .Should().BeNull();
    }

    [Theory]
    [InlineData("")]
    [InlineData("[19:50:42] LocalPlayer: ProcessAddItem(Barley(1), 0, False)")]
    [InlineData("LOADING LEVEL AreaSerbule")]
    [InlineData("[19:50:42] LocalPlayer: ProcessSetCelestialInfo()")]
    public void Returns_null_for_unrelated_or_malformed_lines(string line)
    {
        Parser.TryParse(line, Ts).Should().BeNull();
    }

    [Fact]
    public void DisplayName_is_fixed_for_every_recognised_phase()
    {
        MoonPhase.NewMoon.DisplayName("x").Should().Be("New Moon");
        MoonPhase.WaxingCrescent.DisplayName("x").Should().Be("Waxing Crescent");
        MoonPhase.FirstQuarter.DisplayName("x").Should().Be("First Quarter");
        MoonPhase.WaxingGibbous.DisplayName("x").Should().Be("Waxing Gibbous");
        MoonPhase.FullMoon.DisplayName("x").Should().Be("Full Moon");
        MoonPhase.WaningGibbous.DisplayName("x").Should().Be("Waning Gibbous");
        MoonPhase.ThirdQuarter.DisplayName("x").Should().Be("Third Quarter");
        MoonPhase.WaningCrescent.DisplayName("x").Should().Be("Waning Crescent");
    }
}
