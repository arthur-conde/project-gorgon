using System.Globalization;
using System.Windows.Data;
using System.Windows.Media;
using FluentAssertions;
using Mithril.Shared.Wpf;
using Xunit;

namespace Mithril.Shared.Tests.Wpf;

/// <summary>
/// Coverage for the issue #132 <see cref="ArgbHexToColorConverter"/> — the two-way
/// bridge between an ARGB-hex string property (canonical <c>#AARRGGBB</c>) and a WPF
/// <see cref="Color"/> used to wire <c>mah:ColorPicker.SelectedColor</c> against the
/// existing Legolas hex-string settings. Covers 8-digit ARGB round-trip, 6-digit RGB
/// (alpha defaults to FF on parse), the fail-loud opaque-magenta fallback on malformed
/// input, ConvertBack canonicalisation to uppercase #AARRGGBB, and alpha preservation
/// (including translucent values — the picker's whole reason for existing).
/// </summary>
public sealed class ArgbHexToColorConverterTests
{
    private static readonly ArgbHexToColorConverter Sut = new();

    private static Color Convert(object? value)
        => (Color)Sut.Convert(value!, typeof(Color), null!, CultureInfo.InvariantCulture);

    private static string ConvertBack(Color color)
        => (string)Sut.ConvertBack(color, typeof(string), null!, CultureInfo.InvariantCulture);

    private static object ConvertBackRaw(object? value)
        => Sut.ConvertBack(value!, typeof(string), null!, CultureInfo.InvariantCulture);

    [Theory]
    [InlineData("#FFFF0000", 0xFF, 0xFF, 0x00, 0x00)] // opaque red
    [InlineData("#33FFFF80", 0x33, 0xFF, 0xFF, 0x80)] // translucent yellow (issue example)
    [InlineData("#80808080", 0x80, 0x80, 0x80, 0x80)] // 50% mid-grey
    [InlineData("#00000000", 0x00, 0x00, 0x00, 0x00)] // fully transparent
    public void Convert_parses_eight_digit_argb(string hex, byte a, byte r, byte g, byte b)
    {
        var color = Convert(hex);
        color.A.Should().Be(a);
        color.R.Should().Be(r);
        color.G.Should().Be(g);
        color.B.Should().Be(b);
    }

    [Theory]
    [InlineData("#FF0000", 0xFF, 0x00, 0x00)] // bare RGB → alpha defaults to FF
    [InlineData("#AABBCC", 0xAA, 0xBB, 0xCC)]
    public void Convert_six_digit_rgb_defaults_alpha_to_FF(string hex, byte r, byte g, byte b)
    {
        var color = Convert(hex);
        color.A.Should().Be(0xFF);
        color.R.Should().Be(r);
        color.G.Should().Be(g);
        color.B.Should().Be(b);
    }

    [Theory]
    [InlineData("ff00ff00")] // lower-case, no '#'
    [InlineData("FF00FF00")] // upper-case, no '#'
    public void Convert_accepts_input_without_leading_hash(string hex)
    {
        var color = Convert(hex);
        color.Should().Be(Color.FromArgb(0xFF, 0x00, 0xFF, 0x00));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("not-a-color")]
    [InlineData("#GGGGGG")]     // non-hex digits
    [InlineData("#FFF")]        // wrong length (3)
    [InlineData("#FFFFFFFFF")]  // wrong length (9)
    [InlineData("garbage with #FFFFFFFF inside")]
    public void Convert_returns_opaque_magenta_on_malformed_input(string? hex)
    {
        var color = Convert(hex);
        color.Should().Be(Colors.Magenta);
        color.A.Should().Be(0xFF, "fail-loud fallback must be opaque so a bad value is visible, not silently transparent");
    }

    [Theory]
    [InlineData(0xFF, 0xFF, 0x00, 0x00, "#FFFF0000")]
    [InlineData(0x33, 0xFF, 0xFF, 0x80, "#33FFFF80")]
    [InlineData(0x00, 0x12, 0x34, 0x56, "#00123456")] // pads single-digit hex components with leading zeros
    public void ConvertBack_emits_canonical_uppercase_argb_hex(byte a, byte r, byte g, byte b, string expected)
    {
        ConvertBack(Color.FromArgb(a, r, g, b)).Should().Be(expected);
    }

    [Theory]
    [InlineData("#33FFFF80")]
    [InlineData("#80808080")]
    [InlineData("#01000000")] // Legolas' near-transparent default Fill — must survive the round-trip
    public void Round_trip_preserves_argb_components(string hex)
    {
        var color = Convert(hex);
        ConvertBack(color).Should().Be(hex.ToUpperInvariant());
    }

    [Fact]
    public void Round_trip_preserves_alpha_for_translucent_color()
    {
        // The whole reason #132 needs a real picker: 33-alpha is the "translucent yellow"
        // example from the issue body. Loss of alpha here would defeat the feature.
        var original = Color.FromArgb(0x33, 0xFF, 0xFF, 0x80);
        var hex = ConvertBack(original);
        var roundTripped = Convert(hex);
        roundTripped.Should().Be(original);
    }

    [Fact]
    public void ConvertBack_returns_DoNothing_for_null()
    {
        // MahApps mah:ColorPicker.SelectedColor is typed Color? and dispatches null
        // during initialisation races / mid-edit hex clears / future reset paths.
        // Fabricating a literal hex on null would silently clobber the user's saved
        // color with opaque magenta — the wrong shape of "fail loud" here, because
        // null on ConvertBack is a "no value" signal, not malformed user input.
        // Binding.DoNothing tells WPF to skip the source-property update.
        ConvertBackRaw(null).Should().BeSameAs(Binding.DoNothing);
    }

    [Theory]
    [InlineData("not a color")]
    [InlineData(42)]
    [InlineData(true)]
    public void ConvertBack_returns_DoNothing_for_non_Color_input(object value)
    {
        // Defensive: any non-Color from the binding system (mis-wired template,
        // converter applied to wrong DP) leaves the source property untouched.
        ConvertBackRaw(value).Should().BeSameAs(Binding.DoNothing);
    }
}
