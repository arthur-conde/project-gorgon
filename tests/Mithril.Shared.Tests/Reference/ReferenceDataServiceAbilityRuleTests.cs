using System.IO;
using System.Linq;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using Mithril.Shared.Reference;
using Xunit;

namespace Mithril.Shared.Tests.Reference;

[Trait("Category", "FileIO")]
[Collection("FileIO")]
public sealed class ReferenceDataServiceAbilityRuleTests : IDisposable
{
    private readonly string _root;
    private readonly string _cacheDir;

    public ReferenceDataServiceAbilityRuleTests()
    {
        _root = Mithril.TestSupport.TestPaths.CreateTempDir("mithril-ref-ability-rule-tests");
        _cacheDir = Path.Combine(_root, "cache");
        Directory.CreateDirectory(_cacheDir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { }
    }

    private static HttpClient NoHttp() => new(new ThrowingHandler());

    private ReferenceDataService LoadFromRealBundled()
    {
        var realBundled = Path.Combine(AppContext.BaseDirectory, "Reference", "BundledData");
        return new ReferenceDataService(_cacheDir, NoHttp(), bundledDir: realBundled);
    }

    [Fact]
    public void AbilityKeywordRules_LoadsFromBundled()
    {
        var svc = LoadFromRealBundled();

        svc.AbilityKeywordRules.Should().NotBeEmpty(
            "abilitykeywords.json ships ~30 entries; > 0 catches a wiring regression without locking the exact count against future CDN refreshes.");
        svc.GetSnapshot("abilitykeywords").Source.Should().Be(ReferenceFileSource.Bundled);
    }

    [Fact]
    public void AbilityDynamicDots_LoadsFromBundled()
    {
        var svc = LoadFromRealBundled();

        svc.AbilityDynamicDots.Should().NotBeEmpty();
        svc.GetSnapshot("abilitydynamicdots").Source.Should().Be(ReferenceFileSource.Bundled);
    }

    [Fact]
    public void AbilityDynamicSpecialValues_LoadsFromBundled()
    {
        var svc = LoadFromRealBundled();

        svc.AbilityDynamicSpecialValues.Should().NotBeEmpty();
        svc.GetSnapshot("abilitydynamicspecialvalues").Source.Should().Be(ReferenceFileSource.Bundled);
    }

    [Fact]
    public void AbilityKeyword_PocoCovers_AllAttributesThatFieldsInBundledData()
    {
        // Bundled abilitykeywords.json contains 8 distinct AttributesThat* keys.
        // This regression net catches a future refactor that renames or drops a
        // property and silently breaks the JSON binding (Newtonsoft drops unknown
        // fields without warning). Drift detection for a NEW field landing on
        // disk is a separate concern owned by .github/workflows/cdn-drift-check.yml
        // (see #285).
        var svc = LoadFromRealBundled();
        var rules = svc.AbilityKeywordRules;

        rules.Should().Contain(r => r.AttributesThatDeltaAccuracy != null && r.AttributesThatDeltaAccuracy.Count > 0);
        rules.Should().Contain(r => r.AttributesThatDeltaCritChance != null && r.AttributesThatDeltaCritChance.Count > 0);
        rules.Should().Contain(r => r.AttributesThatDeltaDamage != null && r.AttributesThatDeltaDamage.Count > 0);
        rules.Should().Contain(r => r.AttributesThatDeltaPowerCost != null && r.AttributesThatDeltaPowerCost.Count > 0);
        rules.Should().Contain(r => r.AttributesThatDeltaRange != null && r.AttributesThatDeltaRange.Count > 0);
        rules.Should().Contain(r => r.AttributesThatDeltaResetTime != null && r.AttributesThatDeltaResetTime.Count > 0);
        rules.Should().Contain(r => r.AttributesThatModCritDamage != null && r.AttributesThatModCritDamage.Count > 0);
        rules.Should().Contain(r => r.AttributesThatModDamage != null && r.AttributesThatModDamage.Count > 0);
        rules.Should().Contain(r => r.MustHaveAbilityKeywords != null && r.MustHaveAbilityKeywords.Count > 0);
    }

    [Fact]
    public void AbilityRulePredicate_Matches_UnconstrainedRequiredMatchesAnything()
    {
        AbilityRulePredicate.Matches(required: null, candidate: null).Should().BeTrue();
        AbilityRulePredicate.Matches(required: Array.Empty<string>(), candidate: null).Should().BeTrue();
        AbilityRulePredicate.Matches(required: Array.Empty<string>(), candidate: new[] { "Anything" }).Should().BeTrue();
    }

    [Fact]
    public void AbilityRulePredicate_Matches_SubsetOfCandidateMatches()
    {
        AbilityRulePredicate.Matches(
            required: new[] { "Fire", "Ranged" },
            candidate: new[] { "Fire", "Ranged", "Piercing" })
            .Should().BeTrue();
    }

    [Fact]
    public void AbilityRulePredicate_Matches_MissingTokenDoesNotMatch()
    {
        AbilityRulePredicate.Matches(
            required: new[] { "Fire", "Ranged" },
            candidate: new[] { "Fire" })
            .Should().BeFalse();
        AbilityRulePredicate.Matches(
            required: new[] { "Fire" },
            candidate: null)
            .Should().BeFalse();
        AbilityRulePredicate.Matches(
            required: new[] { "Fire" },
            candidate: Array.Empty<string>())
            .Should().BeFalse();
    }

    [Fact]
    public void AbilityRulePredicate_Matches_IsOrdinal()
    {
        AbilityRulePredicate.Matches(
            required: new[] { "fire" },
            candidate: new[] { "Fire" })
            .Should().BeFalse("ability keyword tokens are case-sensitive identifiers in the JSON, not user-facing text.");
    }

    [Fact]
    public void AbilityRulePredicate_MatchesActiveSkill_NullOrEmptyIsUnconstrained()
    {
        AbilityRulePredicate.MatchesActiveSkill(required: null, candidate: null).Should().BeTrue();
        AbilityRulePredicate.MatchesActiveSkill(required: "", candidate: "Fire").Should().BeTrue();
        AbilityRulePredicate.MatchesActiveSkill(required: "Fire", candidate: "Fire").Should().BeTrue();
        AbilityRulePredicate.MatchesActiveSkill(required: "Fire", candidate: "Ice").Should().BeFalse();
        AbilityRulePredicate.MatchesActiveSkill(required: "Fire", candidate: null).Should().BeFalse();
    }

    private sealed class ThrowingHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => throw new InvalidOperationException("HTTP must not be called during bundled-load tests.");
    }
}
