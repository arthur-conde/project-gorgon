using FluentAssertions;
using Xunit;

namespace Arda.Dispatch.Tests;

public class ArgTokenizerTests
{
    [Fact]
    public void NextLong_ParsesSimpleInteger()
    {
        var tok = new ArgTokenizer("(12345)".AsSpan(), default, "");
        tok.SkipOpen();
        tok.NextLong().Should().Be(12345);
    }

    [Fact]
    public void NextLong_MultipleValues_ParsesInOrder()
    {
        var tok = new ArgTokenizer("(100, 200, 300)".AsSpan(), default, "");
        tok.SkipOpen();
        tok.NextLong().Should().Be(100);
        tok.NextLong().Should().Be(200);
        tok.NextLong().Should().Be(300);
    }

    [Fact]
    public void NextDouble_ParsesDecimal()
    {
        var tok = new ArgTokenizer("(45.75)".AsSpan(), default, "");
        tok.SkipOpen();
        tok.NextDouble().Should().Be(45.75);
    }

    [Fact]
    public void NextBool_ParsesTrueAndFalse()
    {
        var tok = new ArgTokenizer("(True, False)".AsSpan(), default, "");
        tok.SkipOpen();
        tok.NextBool().Should().BeTrue();
        tok.NextBool().Should().BeFalse();
    }

    [Fact]
    public void NextQuotedSpan_ReturnsContentWithoutQuotes()
    {
        var tok = new ArgTokenizer("(\"NPC_Marna\")".AsSpan(), default, "");
        tok.SkipOpen();
        tok.NextQuotedSpan().ToString().Should().Be("NPC_Marna");
    }

    [Fact]
    public void NextQuotedSpan_Unquoted_FallsBackToRawToken()
    {
        var tok = new ArgTokenizer("(Idle)".AsSpan(), default, "");
        tok.SkipOpen();
        tok.NextQuotedSpan().ToString().Should().Be("Idle");
    }

    [Fact]
    public void NextBracedSpan_ReturnsInnerContent()
    {
        var tok = new ArgTokenizer("({type=Toolcrafting,raw=7,bonus=0})".AsSpan(), default, "");
        tok.SkipOpen();
        tok.NextBracedSpan().ToString().Should().Be("type=Toolcrafting,raw=7,bonus=0");
    }

    [Fact]
    public void NextBracedSpan_MultipleBraced_ParsesSequentially()
    {
        var tok = new ArgTokenizer("({a=1,b=2}, {c=3,d=4})".AsSpan(), default, "");
        tok.SkipOpen();
        tok.NextBracedSpan().ToString().Should().Be("a=1,b=2");
        tok.NextBracedSpan().ToString().Should().Be("c=3,d=4");
    }

    [Fact]
    public void NextBracketedSpan_ReturnsInnerContent()
    {
        var tok = new ArgTokenizer("([item1,item2,item3])".AsSpan(), default, "");
        tok.SkipOpen();
        tok.NextBracketedSpan().ToString().Should().Be("item1,item2,item3");
    }

    [Fact]
    public void Skip_SkipsScalars()
    {
        var tok = new ArgTokenizer("(100, 200, 300)".AsSpan(), default, "");
        tok.SkipOpen();
        tok.Skip(2);
        tok.NextLong().Should().Be(300);
    }

    [Fact]
    public void Skip_SkipsQuotedStrings()
    {
        var tok = new ArgTokenizer("(\"hello\", 42)".AsSpan(), default, "");
        tok.SkipOpen();
        tok.Skip(1);
        tok.NextLong().Should().Be(42);
    }

    [Fact]
    public void Skip_SkipsBracedStructs()
    {
        var tok = new ArgTokenizer("({type=X,raw=7}, 99)".AsSpan(), default, "");
        tok.SkipOpen();
        tok.Skip(1);
        tok.NextLong().Should().Be(99);
    }

    [Fact]
    public void Skip_SkipsBracketedArrays()
    {
        var tok = new ArgTokenizer("([a,b,c], 77)".AsSpan(), default, "");
        tok.SkipOpen();
        tok.Skip(1);
        tok.NextLong().Should().Be(77);
    }

    [Fact]
    public void MixedTypes_ParsesCorrectly()
    {
        // Simulates: ProcessStartInteraction(entityId, ?, favor, ?, "NPC_Key")
        var tok = new ArgTokenizer("(500, 0, 45.5, Idle, \"NPC_Marna\")".AsSpan(), default, "");
        tok.SkipOpen();
        tok.NextLong().Should().Be(500);
        tok.Skip(1);
        tok.NextDouble().Should().Be(45.5);
        tok.Skip(1);
        tok.NextQuotedSpan().ToString().Should().Be("NPC_Marna");
    }

    [Fact]
    public void HasMore_TrueWhileContentRemains()
    {
        var tok = new ArgTokenizer("(1, 2)".AsSpan(), default, "");
        tok.SkipOpen();
        tok.HasMore.Should().BeTrue();
        tok.NextLong();
        tok.HasMore.Should().BeTrue();
        tok.NextLong();
        tok.HasMore.Should().BeFalse();
    }

    [Fact]
    public void EmptyArgs_HasMoreIsFalse()
    {
        var tok = new ArgTokenizer("()".AsSpan(), default, "");
        tok.SkipOpen();
        tok.HasMore.Should().BeFalse();
    }

    [Fact]
    public void NestedParensInBraces_HandledCorrectly()
    {
        // Braces containing parens — depth tracking is for braces only
        var tok = new ArgTokenizer("({name=Flower11(Scale=0.9)}, 0)".AsSpan(), default, "");
        tok.SkipOpen();
        tok.NextBracedSpan().ToString().Should().Be("name=Flower11(Scale=0.9)");
        tok.NextLong().Should().Be(0);
    }

    [Fact]
    public void RealWorldAddItem_ParsesCorrectly()
    {
        // ProcessAddItem(GoblinCap(84741837), -1, False)
        var tok = new ArgTokenizer("(GoblinCap(84741837), -1, False)".AsSpan(), default, "");
        tok.SkipOpen();
        // First token includes the parens since NextRawToken stops at ',' or ')'
        // but inner parens aren't balanced by NextRawToken — this is a known limitation
        // for item names with instance IDs. Handlers for AddItem use NextTokenSpan
        // and parse the compound token themselves.
        var itemToken = tok.NextTokenSpan();
        itemToken.ToString().Should().Contain("GoblinCap");
    }
}
