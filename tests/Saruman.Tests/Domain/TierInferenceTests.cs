using FluentAssertions;
using Saruman.Domain;
using Xunit;

namespace Saruman.Tests.Domain;

public sealed class TierInferenceTests
{
    // Tier 1 = 2 syllables. Real codes captured from the user's codebook.
    [Theory]
    [InlineData("FEAVEG")]       // FEA-VEG → 2
    [InlineData("ZOCKZECH")]     // ZOCK-ZECH → 2
    [InlineData("WHUBGLUX")]     // WHUB-GLUX → 2
    [InlineData("HOIMWOB")]      // HOIM-WOB → 2
    [InlineData("TECKPLUE")]     // TECK-PLUE → 2 (UE is one vowel group)
    [InlineData("TEVKUM")]       // TEV-KUM → 2
    [InlineData("BWUBGUCH")]     // BWUB-GUCH → 2
    [InlineData("CHREFLUA")]     // CHRE-FLUA → 2 (UA is one vowel group)
    [InlineData("CLYLLVIZ")]     // CLY-LL-VIZ → Y is vowel here (CL-Y), I → 2
    [InlineData("IILLTREL")]     // II-TREL → 2 (II is one group)
    [InlineData("DRIZWUH")]      // DRI-ZWUH → 2
    [InlineData("WARRTRED")]     // WARR-TRED → 2
    public void Two_syllable_codes_are_tier_1(string code) =>
        TierInference.FromCode(code).Should().Be(WordOfPowerTier.Tier1);

    [Theory]
    [InlineData("PRYSGWIMLIK")]  // PRY-GWIM-LIK → 3 (Y vowel, I, I)
    public void Three_syllable_codes_are_tier_2(string code) =>
        TierInference.FromCode(code).Should().Be(WordOfPowerTier.Tier2);

    [Fact]
    public void Empty_code_returns_tier_1()
    {
        TierInference.FromCode("").Should().Be(WordOfPowerTier.Tier1);
    }
}
