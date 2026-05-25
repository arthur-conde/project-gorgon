using FluentAssertions;
using Xunit;

namespace Arda.Dispatch.Tests;

public class VerbExtractorTests
{
    [Theory]
    [InlineData("LocalPlayer: ProcessDeleteItem(12345)", "ProcessDeleteItem")]
    [InlineData("LocalPlayer: ProcessAddItem(GoblinCap(84741837), -1, False)", "ProcessAddItem")]
    [InlineData("LocalPlayer: ProcessUpdateSkill(Toolcrafting, 7)", "ProcessUpdateSkill")]
    [InlineData("LocalPlayer: ProcessStartInteraction(123, 0, 45.5, Idle, \"NPC_Marna\")", "ProcessStartInteraction")]
    public void Extract_ProcessVerb_ReturnsVerbName(string line, string expectedVerb)
    {
        var result = VerbExtractor.Extract(line.AsSpan());
        result.ToString().Should().Be(expectedVerb);
    }

    [Fact]
    public void Extract_LoadingLevel_ReturnsSyntheticKey()
    {
        var result = VerbExtractor.Extract("LOADING LEVEL AreaSerbule".AsSpan());
        result.ToString().Should().Be(Verbs.LoadingLevel);
    }

    [Fact]
    public void Extract_InitializingArea_ReturnsSyntheticKey()
    {
        var result = VerbExtractor.Extract("!!! Initializing area! (502934): AreaSerbule".AsSpan());
        result.ToString().Should().Be(Verbs.InitializingArea);
    }

    [Theory]
    [InlineData("")]
    [InlineData("some random text")]
    [InlineData("Download appearance loop @model(params)")]
    public void Extract_UnrecognizedLine_ReturnsEmpty(string line)
    {
        var result = VerbExtractor.Extract(line.AsSpan());
        result.IsEmpty.Should().BeTrue();
    }

    [Fact]
    public void Extract_LocalPlayerPrefix_NoParens_ReturnsWholeAfterPrefix()
    {
        var result = VerbExtractor.Extract("LocalPlayer: SomethingWithoutParens".AsSpan());
        result.ToString().Should().Be("SomethingWithoutParens");
    }

    [Fact]
    public void ExtractArgs_ProcessVerb_ReturnsArgsIncludingParen()
    {
        var result = VerbExtractor.ExtractArgs("LocalPlayer: ProcessDeleteItem(12345, True)".AsSpan());
        result.ToString().Should().Be("(12345, True)");
    }

    [Fact]
    public void ExtractArgs_LoadingLevel_ReturnsAreaKey()
    {
        var result = VerbExtractor.ExtractArgs("LOADING LEVEL AreaSerbule".AsSpan());
        result.ToString().Should().Be("AreaSerbule");
    }

    [Fact]
    public void ExtractArgs_InitializingArea_ReturnsContent()
    {
        var result = VerbExtractor.ExtractArgs("!!! Initializing area! (502934): AreaSerbule".AsSpan());
        result.ToString().Should().Be("(502934): AreaSerbule");
    }

    [Fact]
    public void ExtractArgs_UnrecognizedLine_ReturnsEmpty()
    {
        var result = VerbExtractor.ExtractArgs("some random text".AsSpan());
        result.IsEmpty.Should().BeTrue();
    }
}
