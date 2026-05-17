using System.Linq;
using System.Windows.Documents;
using FluentAssertions;
using Mithril.Shared.Wpf;
using Xunit;

namespace Mithril.Shared.Tests.Wpf;

/// <summary>
/// Pure-logic tests for the G4 Structure-tier tracked-uppercase transform. Mirrors
/// <see cref="FormattedTextRendererTests"/>: asserts the
/// <see cref="StructureLabel.BuildTrackedInlines"/> / <see cref="StructureLabel.TrackedText"/>
/// contract without instantiating a TextBlock or touching the visual tree.
/// </summary>
public sealed class StructureLabelBehaviorTests
{
    private const char Hair = StructureLabel.HairSpace;

    private static string Concat(System.Collections.Generic.IEnumerable<Inline> inlines) =>
        string.Concat(inlines.OfType<Run>().Select(r => r.Text));

    [Fact]
    public void UpperCases_Invariant()
    {
        StructureLabel.TrackedText("Requirements")
            .Replace(Hair.ToString(), "")
            .Should().Be("REQUIREMENTS");

        Concat(StructureLabel.BuildTrackedInlines("Ingredients"))
            .Replace(Hair.ToString(), "")
            .Should().Be("INGREDIENTS");
    }

    [Fact]
    public void InsertsHairSpace_BetweenEveryAdjacentPair()
    {
        // "ABC" → A <hair> B <hair> C : 3 letter runs + 2 spacers = 5 inlines.
        var inlines = StructureLabel.BuildTrackedInlines("abc");

        inlines.Should().HaveCount(5);
        inlines.OfType<Run>().Select(r => r.Text)
            .Should().Equal("A", Hair.ToString(), "B", Hair.ToString(), "C");

        StructureLabel.TrackedText("abc").Should().Be($"A{Hair}B{Hair}C");
    }

    [Fact]
    public void Spacing_ScalesWithFontSize_BecauseHairSpaceIsEmRelative()
    {
        // The spacer is a hair space whose advance is defined relative to the font em, so
        // tracking scales with whatever FontSize the runs inherit — there is no px width to
        // assert. The contract under test: exactly one hair-space spacer per inter-character
        // gap, so the *number* of em-relative spacers scales 1:1 with label length (and the
        // rendered width therefore scales with the inherited FontSize the runs carry).
        var spacerCount = StructureLabel.BuildTrackedInlines("Taught by")
            .OfType<Run>()
            .Count(r => r.Text == Hair.ToString());

        // "Taught by" = 9 chars → 8 inter-character gaps → 8 em-relative spacers.
        spacerCount.Should().Be("Taught by".Length - 1);
    }

    [Fact]
    public void TrailingColon_IsPreserved_AndTracksLikeALetter()
    {
        // StructureInlinePrefixStyle idiom: "Field:" — the ':' is call-site text. It must
        // survive (ToUpperInvariant is a no-op on punctuation) AND get a leading hair-space
        // so it reads as part of the same tracked label, not a tight-set afterthought.
        var tracked = StructureLabel.TrackedText("Skill:");

        tracked.Should().Be($"S{Hair}K{Hair}I{Hair}L{Hair}L{Hair}:");
        tracked.Replace(Hair.ToString(), "").Should().Be("SKILL:");

        var inlines = StructureLabel.BuildTrackedInlines("Skill:");
        inlines.OfType<Run>().Last().Text.Should().Be(":");
        // The ':' is preceded by a spacer (tracks consistently with the letters).
        inlines.OfType<Run>().Reverse().Skip(1).First().Text.Should().Be(Hair.ToString());
    }

    [Fact]
    public void SingleCharacter_HasNoSpacer()
    {
        StructureLabel.BuildTrackedInlines("x").Should().ContainSingle()
            .Which.As<Run>().Text.Should().Be("X");
        StructureLabel.TrackedText("x").Should().Be("X");
    }

    [Fact]
    public void NullOrEmpty_IsSafe()
    {
        StructureLabel.BuildTrackedInlines(null).Should().BeEmpty();
        StructureLabel.BuildTrackedInlines("").Should().BeEmpty();
        StructureLabel.TrackedText(null).Should().BeEmpty();
        StructureLabel.TrackedText("").Should().BeEmpty();
    }

    [Fact]
    public void TrackedText_EqualsConcatenatedInlineRunText()
    {
        // The string helper and the inline builder must agree (one is the other flattened).
        const string sample = "Other Requirements:";
        Concat(StructureLabel.BuildTrackedInlines(sample))
            .Should().Be(StructureLabel.TrackedText(sample));
    }
}
