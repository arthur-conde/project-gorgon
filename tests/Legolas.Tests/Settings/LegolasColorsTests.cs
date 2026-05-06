using FluentAssertions;
using Legolas.Domain;

namespace Legolas.Tests.Settings;

public class LegolasColorsTests
{
    [Theory]
    [InlineData("#FFFF0000", "#FFFF0000")]
    [InlineData("#ff00ff00", "#FF00FF00")]
    [InlineData("FF0000", "#FFFF0000")]      // 6-digit RGB → alpha defaults to FF
    [InlineData("#abcdef", "#FFABCDEF")]
    [InlineData("#33FFFF80", "#33FFFF80")]   // translucent preserved
    public void Normalize_canonicalizes_hex(string input, string expected)
    {
        LegolasColors.Normalize(input).Should().Be(expected);
    }

    [Theory]
    [InlineData("not a color")]
    [InlineData("")]
    [InlineData(null)]
    [InlineData("#XYZ")]
    [InlineData("#GG0000")]
    [InlineData("#FFF")]              // 3-digit not supported
    [InlineData("#FFFFFFFFF")]        // too long
    public void Normalize_falls_back_to_magenta_on_invalid(string? input)
    {
        LegolasColors.Normalize(input).Should().Be("#FFFF00FF");
    }

    [Fact]
    public void Setter_normalizes_and_raises_property_changed()
    {
        var colors = new LegolasColors();
        var changed = new List<string?>();
        colors.PropertyChanged += (_, e) => changed.Add(e.PropertyName);

        colors.PinPending = "00ff00"; // lowercase, no hash, RGB

        colors.PinPending.Should().Be("#FF00FF00");
        changed.Should().ContainSingle().Which.Should().Be(nameof(LegolasColors.PinPending));
    }

    [Fact]
    public void Setter_no_op_when_canonical_value_unchanged()
    {
        var colors = new LegolasColors();
        colors.PinPending = "#FF00FFFF"; // matches default after normalization
        var changed = new List<string?>();
        colors.PropertyChanged += (_, e) => changed.Add(e.PropertyName);

        colors.PinPending = "00ffff"; // canonicalizes to same value

        changed.Should().BeEmpty();
    }
}
