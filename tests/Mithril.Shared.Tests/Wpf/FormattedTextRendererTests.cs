using System.Windows;
using System.Windows.Documents;
using FluentAssertions;
using Mithril.Shared.Wpf;
using Xunit;

namespace Mithril.Shared.Tests.Wpf;

public sealed class FormattedTextRendererTests
{
    [Fact]
    public void PlainText_ProducesSingleRun_NoStyling()
    {
        var inlines = FormattedText.BuildInlines("Hello world");

        inlines.Should().ContainSingle();
        var run = inlines.Single().Should().BeOfType<Run>().Subject;
        run.Text.Should().Be("Hello world");
        run.FontStyle.Should().Be(FontStyles.Normal);
        run.FontWeight.Should().Be(FontWeights.Normal);
    }

    [Fact]
    public void ItalicTag_WrapsSpan_AsItalicRun()
    {
        // The dominant PG quest-text pattern: <i>Speaker:</i> body...
        var inlines = FormattedText.BuildInlines("<i>Zhia Lian:</i> Hello adventurer.");

        inlines.Should().HaveCount(2);
        var italic = inlines[0].Should().BeOfType<Run>().Subject;
        italic.Text.Should().Be("Zhia Lian:");
        italic.FontStyle.Should().Be(FontStyles.Italic);
        var rest = inlines[1].Should().BeOfType<Run>().Subject;
        rest.Text.Should().Be(" Hello adventurer.");
        rest.FontStyle.Should().Be(FontStyles.Normal);
    }

    [Fact]
    public void BoldTag_WrapsSpan_AsBoldRun()
    {
        var inlines = FormattedText.BuildInlines("Quest: <b>Important!</b>");

        inlines.Should().HaveCount(2);
        inlines[0].As<Run>().Text.Should().Be("Quest: ");
        var bold = inlines[1].Should().BeOfType<Run>().Subject;
        bold.Text.Should().Be("Important!");
        bold.FontWeight.Should().Be(FontWeights.Bold);
    }

    [Fact]
    public void NestedTags_BothApplyToInnerSpan()
    {
        var inlines = FormattedText.BuildInlines("<b><i>Whisper</i></b> she said");

        inlines.Should().HaveCount(2);
        var whisper = inlines[0].Should().BeOfType<Run>().Subject;
        whisper.Text.Should().Be("Whisper");
        whisper.FontStyle.Should().Be(FontStyles.Italic);
        whisper.FontWeight.Should().Be(FontWeights.Bold);
        inlines[1].As<Run>().Text.Should().Be(" she said");
    }

    [Fact]
    public void UnbalancedOpen_AppliesStyleToEndOfString()
    {
        // Drift safety: a stray <i> without a matching </i> shouldn't crash; the italic
        // run just extends to the end of the input.
        var inlines = FormattedText.BuildInlines("normal <i>italic to end");

        inlines.Should().HaveCount(2);
        inlines[0].As<Run>().Text.Should().Be("normal ");
        inlines[0].As<Run>().FontStyle.Should().Be(FontStyles.Normal);
        inlines[1].As<Run>().Text.Should().Be("italic to end");
        inlines[1].As<Run>().FontStyle.Should().Be(FontStyles.Italic);
    }

    [Fact]
    public void UnbalancedClose_IsClampedAtZero_NotANegativeDepth()
    {
        // Stray </i> with no matching open simply has no effect.
        var inlines = FormattedText.BuildInlines("plain </i> still plain");

        // Three runs: "plain ", "" (the empty segment around the close tag — emitted because
        // the parser doesn't try to coalesce adjacent unstyled runs, which is fine for the
        // TextBlock renderer), " still plain". Or two if the empty segment is skipped — both
        // shapes are acceptable. Assert on the concatenated text rather than count.
        var combined = string.Concat(inlines.OfType<Run>().Select(r => r.Text));
        combined.Should().Be("plain  still plain");
        inlines.Should().AllSatisfy(i => i.As<Run>().FontStyle.Should().Be(FontStyles.Normal));
    }

    [Fact]
    public void Empty_ReturnsEmpty()
    {
        FormattedText.BuildInlines("").Should().BeEmpty();
    }

    [Fact]
    public void NoTags_PreservesAngleBracketsAsLiteralText()
    {
        // The parser only recognises the four supported tag literals — other angle-bracket
        // content (e.g. "5 < 10") passes through verbatim.
        var inlines = FormattedText.BuildInlines("Count: 5 < 10 < 100");

        inlines.Should().ContainSingle();
        inlines[0].As<Run>().Text.Should().Be("Count: 5 < 10 < 100");
    }

    [Fact]
    public void MultipleItalicSegments_ProduceAlternatingRuns()
    {
        var inlines = FormattedText.BuildInlines("<i>A</i> then <i>B</i>");

        inlines.Should().HaveCount(3);
        inlines[0].As<Run>().Text.Should().Be("A");
        inlines[0].As<Run>().FontStyle.Should().Be(FontStyles.Italic);
        inlines[1].As<Run>().Text.Should().Be(" then ");
        inlines[1].As<Run>().FontStyle.Should().Be(FontStyles.Normal);
        inlines[2].As<Run>().Text.Should().Be("B");
        inlines[2].As<Run>().FontStyle.Should().Be(FontStyles.Italic);
    }
}
