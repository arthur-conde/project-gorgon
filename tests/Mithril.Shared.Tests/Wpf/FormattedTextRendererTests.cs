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

    // ── #247: <h1> / <hr> / <br> structural-tag extension (Option A) ──

    [Fact]
    public void Heading_RendersBoldLargerRun_BetweenLineBreaks()
    {
        // The dominant lorebook-body pattern: <h1>Title</h1>\n\nbody...
        var inlines = FormattedText.BuildInlines("<h1>The Wasted Wishes</h1>Body text.");

        // LineBreak, heading Run, LineBreak, LineBreak, body Run.
        inlines.Should().HaveCount(5);
        inlines[0].Should().BeOfType<LineBreak>();
        var heading = inlines[1].Should().BeOfType<Run>().Subject;
        heading.Text.Should().Be("The Wasted Wishes");
        heading.FontWeight.Should().Be(FontWeights.Bold);
        heading.FontSize.Should().BeGreaterThan(SystemFonts.MessageFontSize);
        inlines[2].Should().BeOfType<LineBreak>();
        inlines[3].Should().BeOfType<LineBreak>();
        var body = inlines[4].Should().BeOfType<Run>().Subject;
        body.Text.Should().Be("Body text.");
        body.FontWeight.Should().Be(FontWeights.Normal);
    }

    [Fact]
    public void Heading_Unclosed_FallsBackGracefully_AppliesToEnd()
    {
        // Defensive: a stray <h1> with no </h1> shouldn't crash; the heading style just
        // extends to the end of the input (mirrors the unbalanced-<i> contract).
        var inlines = FormattedText.BuildInlines("intro <h1>title to end");

        var combined = string.Concat(inlines.OfType<Run>().Select(r => r.Text));
        combined.Should().Be("intro title to end");
        var last = inlines.OfType<Run>().Last();
        last.Text.Should().Be("title to end");
        last.FontWeight.Should().Be(FontWeights.Bold);
    }

    [Fact]
    public void MalformedHeading_NoCloseAngle_PassesThroughAsLiteral()
    {
        // "<h1" without a '>' is not a recognised tag literal — passes through verbatim,
        // same contract as a bare '<' in "5 < 10".
        var inlines = FormattedText.BuildInlines("a <h1 b");

        inlines.Should().ContainSingle();
        inlines[0].As<Run>().Text.Should().Be("a <h1 b");
    }

    [Fact]
    public void Break_RendersLineBreak()
    {
        var inlines = FormattedText.BuildInlines("line one<br>line two");

        inlines.Should().HaveCount(3);
        inlines[0].As<Run>().Text.Should().Be("line one");
        inlines[1].Should().BeOfType<LineBreak>();
        inlines[2].As<Run>().Text.Should().Be("line two");
    }

    [Fact]
    public void Break_SelfClosingForm_AlsoRendersLineBreak()
    {
        var inlines = FormattedText.BuildInlines("a<br/>b");

        inlines.Should().HaveCount(3);
        inlines[0].As<Run>().Text.Should().Be("a");
        inlines[1].Should().BeOfType<LineBreak>();
        inlines[2].As<Run>().Text.Should().Be("b");
    }

    [Fact]
    public void HorizontalRule_RendersSeparatorRow_BetweenLineBreaks()
    {
        var inlines = FormattedText.BuildInlines("before<hr>after");

        // Run "before", LineBreak, em-dash Run, LineBreak, LineBreak, Run "after".
        inlines.Should().HaveCount(6);
        inlines[0].As<Run>().Text.Should().Be("before");
        inlines[1].Should().BeOfType<LineBreak>();
        var rule = inlines[2].Should().BeOfType<Run>().Subject;
        rule.Text.Should().Contain("—");
        inlines[3].Should().BeOfType<LineBreak>();
        inlines[4].Should().BeOfType<LineBreak>();
        inlines[5].As<Run>().Text.Should().Be("after");
    }

    [Fact]
    public void HorizontalRule_SelfClosingForm_AlsoRenders()
    {
        var inlines = FormattedText.BuildInlines("x<hr/>y");

        inlines.OfType<Run>().Should().Contain(r => r.Text.Contains("—"));
        inlines.OfType<LineBreak>().Should().NotBeEmpty();
    }

    [Fact]
    public void HeadingWithNestedBold_KeepsBoldAndHeadingStyling()
    {
        // <h1><b>X</b></h1> — the inner span is bold (from both the heading and the explicit
        // <b>); the heading run is also enlarged.
        var inlines = FormattedText.BuildInlines("<h1><b>Chalice Saga</b></h1>");

        var heading = inlines.OfType<Run>().Single();
        heading.Text.Should().Be("Chalice Saga");
        heading.FontWeight.Should().Be(FontWeights.Bold);
        heading.FontSize.Should().BeGreaterThan(SystemFonts.MessageFontSize);
    }

    [Fact]
    public void HeadingPlusBodyWithItalicSpeaker_RealLorebookShape()
    {
        // Realistic lorebook body: heading, then prose carrying an inline <i> span.
        var inlines = FormattedText.BuildInlines(
            "<h1>Notes</h1>The sign reads: <i>Beware the gorgon.</i>");

        var runs = inlines.OfType<Run>().ToList();
        runs.Should().Contain(r => r.Text == "Notes" && r.FontWeight == FontWeights.Bold);
        runs.Should().Contain(r => r.Text == "Beware the gorgon." && r.FontStyle == FontStyles.Italic);
        // No literal tag noise survived.
        string.Concat(runs.Select(r => r.Text)).Should().NotContain("<");
    }
}
