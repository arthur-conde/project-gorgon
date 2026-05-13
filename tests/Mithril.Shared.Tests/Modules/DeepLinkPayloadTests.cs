using FluentAssertions;
using Mithril.Shared.Modules;
using Xunit;

namespace Mithril.Shared.Tests.Modules;

public class DeepLinkPayloadTests
{
    [Theory]
    [InlineData("CraftedLeatherBoots5")]
    [InlineData("Bread")]
    [InlineData("MakeTomatoSauce")]
    [InlineData("A")]                            // 1-char lower bound
    [InlineData("snake_case_name")]              // underscore allowed
    [InlineData("123Numeric")]                   // leading digit allowed
    public void IsValidInternalName_Accepts_TypicalInternalNames(string name)
    {
        DeepLinkPayload.IsValidInternalName(name).Should().BeTrue();
    }

    [Fact]
    public void IsValidInternalName_Accepts_128CharBoundary()
    {
        DeepLinkPayload.IsValidInternalName(new string('A', 128)).Should().BeTrue();
    }

    [Theory]
    [InlineData("")]                             // empty
    [InlineData("has space")]                    // whitespace
    [InlineData("has-hyphen")]                   // hyphen not in alphabet
    [InlineData("has.dot")]                      // dot not in alphabet
    [InlineData("has/slash")]                    // slash not in alphabet
    [InlineData("has\ttab")]                     // tab
    public void IsValidInternalName_Rejects_NamesWithIllegalChars(string name)
    {
        DeepLinkPayload.IsValidInternalName(name).Should().BeFalse();
    }

    [Fact]
    public void IsValidInternalName_Rejects_OverLengthCap()
    {
        DeepLinkPayload.IsValidInternalName(new string('A', 129)).Should().BeFalse();
    }
}
