using System.Globalization;
using FluentAssertions;
using Mithril.Shared.Wpf;
using Xunit;

namespace Mithril.Shared.Tests.Wpf;

/// <summary>
/// Pure-logic coverage for the G3-amend-2 <see cref="FontSizeTimesConverter"/> — the
/// single "<c>FontSize × n</c>" mechanism every Silmarillion grammar tier sizes itself
/// in em through (<c>docs/silmarillion-visual-grammar.md</c> · "All sizes are
/// em-relative, not px"). Mirrors <see cref="LinkTests"/>'s no-UI-spin-up style.
/// Covers value×factor, culture-invariant string-parameter parse, and the
/// null / non-numeric / non-positive safety degrade (a missing inherited FontSize must
/// never crash a detail pane).
/// </summary>
public sealed class FontSizeTimesConverterTests
{
    private static readonly FontSizeTimesConverter Sut = new();

    private static object Convert(object value, object parameter, CultureInfo? culture = null)
        => Sut.Convert(value, typeof(double), parameter, culture ?? CultureInfo.InvariantCulture);

    // ── value × factor (the ratified per-tier factors) ──

    [Theory]
    [InlineData(12.0, "1.5", 18.0)]    // Fact-title / Link list sprite 1.5em @ 12pt
    [InlineData(12.0, "1.0", 12.0)]    // Link prose sprite 1.0em
    [InlineData(12.0, "0.75", 9.0)]    // Link prose Lucide 0.75em
    [InlineData(12.0, "1.125", 13.5)]  // Link list Lucide 1.125em
    [InlineData(12.0, "0.9", 10.8)]    // Stat strip 0.9em
    [InlineData(12.0, "0.78", 9.36)]   // Footer ID / Structure 0.78em
    [InlineData(15.0, "1.5", 22.5)]    // tracks the Appearance slider (15pt)
    [InlineData(18.0, "0.75", 13.5)]   // and at 18pt
    public void Convert_MultipliesFontSizeByFactor(
        double fontSize, string factor, double expected)
    {
        Convert(fontSize, factor).Should().Be(expected);
    }

    [Fact]
    public void Convert_AcceptsNumericParameter_NotJustString()
    {
        Convert(12.0, 1.5).Should().Be(18.0);
        Convert(12.0, 2).Should().Be(24.0);
    }

    [Fact]
    public void Convert_AcceptsIntAndFloatValue()
    {
        Convert(12, "1.5").Should().Be(18.0);
        Convert(12.0f, "0.5").Should().Be(6.0);
    }

    // ── culture-invariant parameter parse (the comma-decimal-locale footgun) ──

    [Fact]
    public void Convert_ParsesDottedParameter_UnderCommaDecimalCulture()
    {
        // de-DE uses ',' as the decimal separator. A XAML ConverterParameter=1.125
        // must NOT be misread as 1125 under that UI culture.
        var de = CultureInfo.GetCultureInfo("de-DE");
        Convert(16.0, "1.125", de).Should().Be(18.0);
    }

    // ── null / non-numeric / non-positive safety (never throw) ──

    [Fact]
    public void Convert_NullValue_DegradesToNaN_NoThrow()
    {
        Convert(null!, "1.5").Should().Be(double.NaN);
    }

    [Fact]
    public void Convert_NonNumericValue_DegradesToNaN()
    {
        Convert("not-a-size", "1.5").Should().Be(double.NaN);
    }

    [Theory]
    [InlineData(0.0)]
    [InlineData(-3.0)]
    [InlineData(double.NaN)]
    [InlineData(double.PositiveInfinity)]
    public void Convert_NonPositiveOrNonFiniteFontSize_DegradesToNaN(double fontSize)
    {
        Convert(fontSize, "1.5").Should().Be(double.NaN);
    }

    [Fact]
    public void Convert_NullOrUnparseableParameter_DegradesToNaN()
    {
        Convert(12.0, null!).Should().Be(double.NaN);
        Convert(12.0, "xyz").Should().Be(double.NaN);
    }

    [Fact]
    public void Scale_PureHelper_MatchesConvert()
    {
        FontSizeTimesConverter.Scale(12.0, 1.5).Should().Be(18.0);
        FontSizeTimesConverter.Scale(0.0, 1.5).Should().Be(double.NaN);
        FontSizeTimesConverter.Scale(double.NaN, 1.0).Should().Be(double.NaN);
    }

    [Fact]
    public void ConvertBack_IsNotSupported()
    {
        var act = () => Sut.ConvertBack(18.0, typeof(double), "1.5", CultureInfo.InvariantCulture);
        act.Should().Throw<NotSupportedException>();
    }
}
