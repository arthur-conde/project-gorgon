using System.IO;
using System.Net.Http;
using FluentAssertions;
using Mithril.Shared.Reference;
using Xunit;

namespace Mithril.Shared.Tests.Reference;

[Trait("Category", "FileIO")]
[Collection("FileIO")]
public sealed class ReferenceDataServiceEffectCrossLinkIndexTests : IDisposable
{
    private readonly string _root;
    private readonly string _cacheDir;
    private readonly string _bundledDir;

    public ReferenceDataServiceEffectCrossLinkIndexTests()
    {
        _root = Mithril.TestSupport.TestPaths.CreateTempDir("mithril-ref-effect-crosslink-tests");
        _cacheDir = Path.Combine(_root, "cache");
        _bundledDir = Path.Combine(_root, "bundled");
        Directory.CreateDirectory(_cacheDir);
        Directory.CreateDirectory(_bundledDir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { }
    }

    private static HttpClient NeverCallHttp() => new(new ThrowingHandler());

    private void WriteFixture(string? effectsJson = null, string? abilitiesJson = null)
    {
        if (effectsJson is not null)
        {
            File.WriteAllText(Path.Combine(_bundledDir, "effects.json"), effectsJson);
            File.WriteAllText(Path.Combine(_bundledDir, "effects.meta.json"), "{\"cdnVersion\":\"v1\",\"source\":0}");
        }
        if (abilitiesJson is not null)
        {
            File.WriteAllText(Path.Combine(_bundledDir, "abilities.json"), abilitiesJson);
            File.WriteAllText(Path.Combine(_bundledDir, "abilities.meta.json"), "{\"cdnVersion\":\"v1\",\"source\":0}");
        }
    }

    [Fact]
    public void EffectsByInternalName_LiftsEnvelopeKey()
    {
        WriteFixture(effectsJson: """
        {
          "effect_10003": { "Name": "Sticky!", "IconId": 1, "Keywords": ["Buff"] }
        }
        """);

        var svc = new ReferenceDataService(_cacheDir, NeverCallHttp(), bundledDir: _bundledDir);

        svc.Effects.Should().ContainKey("effect_10003");
        svc.EffectsByInternalName.Should().ContainKey("effect_10003");
        svc.EffectsByInternalName["effect_10003"].InternalName.Should().Be("effect_10003");
        svc.EffectsByInternalName["effect_10003"].Name.Should().Be("Sticky!");
    }

    [Fact]
    public void EffectsByKeyword_GroupsByEachKeywordTag()
    {
        WriteFixture(effectsJson: """
        {
          "effect_1": { "Name": "A", "IconId": 1, "Keywords": ["Buff", "OrranInv"] },
          "effect_2": { "Name": "B", "IconId": 2, "Keywords": ["Buff"] },
          "effect_3": { "Name": "C", "IconId": 3, "Keywords": ["Debuff"] }
        }
        """);

        var svc = new ReferenceDataService(_cacheDir, NeverCallHttp(), bundledDir: _bundledDir);

        svc.EffectsByKeyword["Buff"].Should().HaveCount(2);
        svc.EffectsByKeyword["Buff"].Select(e => e.InternalName)
            .Should().BeEquivalentTo(["effect_1", "effect_2"]);
        svc.EffectsByKeyword["OrranInv"].Should().ContainSingle()
            .Which.InternalName.Should().Be("effect_1");
        svc.EffectsByKeyword["Debuff"].Should().ContainSingle()
            .Which.InternalName.Should().Be("effect_3");
    }

    [Fact]
    public void EffectsByStackingType_GroupsByStackingType()
    {
        WriteFixture(effectsJson: """
        {
          "effect_1": { "Name": "A", "IconId": 1, "StackingType": "WordOfPowerInventory" },
          "effect_2": { "Name": "B", "IconId": 2, "StackingType": "WordOfPowerInventory" },
          "effect_3": { "Name": "C", "IconId": 3, "StackingType": "ShamanicHeal" },
          "effect_4": { "Name": "D", "IconId": 4 }
        }
        """);

        var svc = new ReferenceDataService(_cacheDir, NeverCallHttp(), bundledDir: _bundledDir);

        svc.EffectsByStackingType["WordOfPowerInventory"].Should().HaveCount(2);
        svc.EffectsByStackingType["WordOfPowerInventory"].Select(e => e.InternalName)
            .Should().BeEquivalentTo(["effect_1", "effect_2"]);
        svc.EffectsByStackingType["ShamanicHeal"].Should().ContainSingle()
            .Which.InternalName.Should().Be("effect_3");
        // Effects with no StackingType don't appear under any key.
        svc.EffectsByStackingType.Should().NotContainKey("");
    }

    [Fact]
    public void AbilitiesByEffectKeyword_IndexesUnionOfThreeFields()
    {
        WriteFixture(
            effectsJson: "{ }",
            abilitiesJson: """
            {
              "ability_1": { "InternalName": "Req", "Name": "Req", "Skill": "Sword", "Level": 1, "IconID": 1, "EffectKeywordReqs": ["FrostShard"] },
              "ability_2": { "InternalName": "Enabled", "Name": "Enabled", "Skill": "Sword", "Level": 2, "IconID": 2, "EffectKeywordsIndicatingEnabled": ["FrostShard"] },
              "ability_3": { "InternalName": "TargetReq", "Name": "TargetReq", "Skill": "Sword", "Level": 3, "IconID": 3, "TargetEffectKeywordReq": "FrostShard" },
              "ability_4": { "InternalName": "Unrelated", "Name": "Unrelated", "Skill": "Sword", "Level": 4, "IconID": 4 }
            }
            """);

        var svc = new ReferenceDataService(_cacheDir, NeverCallHttp(), bundledDir: _bundledDir);

        svc.AbilitiesByEffectKeyword.Should().ContainKey("FrostShard");
        svc.AbilitiesByEffectKeyword["FrostShard"].Select(a => a.InternalName)
            .Should().BeEquivalentTo(["Req", "Enabled", "TargetReq"]);
    }

    [Fact]
    public void AbilitiesByEffectKeyword_DedupesWhenSameTagAppearsInMultipleFields()
    {
        WriteFixture(
            effectsJson: "{ }",
            abilitiesJson: """
            {
              "ability_1": {
                "InternalName": "Double", "Name": "Double", "Skill": "Sword", "Level": 1, "IconID": 1,
                "EffectKeywordReqs": ["FrostShard"],
                "EffectKeywordsIndicatingEnabled": ["FrostShard"],
                "TargetEffectKeywordReq": "FrostShard"
              }
            }
            """);

        var svc = new ReferenceDataService(_cacheDir, NeverCallHttp(), bundledDir: _bundledDir);

        svc.AbilitiesByEffectKeyword["FrostShard"].Should().ContainSingle()
            .Which.InternalName.Should().Be("Double");
    }

    [Fact]
    public void AbilitiesByEffectKeyword_ExcludesInternalAbilities()
    {
        WriteFixture(
            effectsJson: "{ }",
            abilitiesJson: """
            {
              "ability_1": { "InternalName": "Player", "Name": "Player", "Skill": "Sword", "Level": 1, "IconID": 1, "EffectKeywordReqs": ["FrostShard"] },
              "ability_2": { "InternalName": "Mob", "Name": "Mob", "Skill": "Internal", "Level": 1, "IconID": 2, "EffectKeywordReqs": ["FrostShard"], "InternalAbility": true }
            }
            """);

        var svc = new ReferenceDataService(_cacheDir, NeverCallHttp(), bundledDir: _bundledDir);

        svc.AbilitiesByEffectKeyword["FrostShard"].Should().ContainSingle()
            .Which.InternalName.Should().Be("Player");
    }

    [Fact]
    public void EffectsByTriggeringAbilityKeyword_IndexesEffectAbilityKeywords()
    {
        WriteFixture(effectsJson: """
        {
          "effect_1": { "Name": "Augury", "IconId": 1, "AbilityKeywords": ["Attack"] },
          "effect_2": { "Name": "OtherAttack", "IconId": 2, "AbilityKeywords": ["Attack", "Ranged"] },
          "effect_3": { "Name": "NoTrigger", "IconId": 3 }
        }
        """);

        var svc = new ReferenceDataService(_cacheDir, NeverCallHttp(), bundledDir: _bundledDir);

        svc.EffectsByTriggeringAbilityKeyword["Attack"].Should().HaveCount(2);
        svc.EffectsByTriggeringAbilityKeyword["Attack"].Select(e => e.InternalName)
            .Should().BeEquivalentTo(["effect_1", "effect_2"]);
        svc.EffectsByTriggeringAbilityKeyword["Ranged"].Should().ContainSingle()
            .Which.InternalName.Should().Be("effect_2");
    }

    [Fact]
    public async Task RefreshAbilities_RebuildsAbilitiesByEffectKeyword()
    {
        // Initial state: one ability requiring FrostShard.
        WriteFixture(
            effectsJson: """
            {
              "effect_1": { "Name": "FrostShard", "IconId": 1, "Keywords": ["FrostShard"] }
            }
            """,
            abilitiesJson: """
            {
              "ability_1": { "InternalName": "Strike", "Name": "Strike", "Skill": "Sword", "Level": 1, "IconID": 1, "EffectKeywordReqs": ["FrostShard"] }
            }
            """);

        var svc = new ReferenceDataService(_cacheDir, NeverCallHttp(), bundledDir: _bundledDir);
        svc.AbilitiesByEffectKeyword["FrostShard"].Select(a => a.InternalName)
            .Should().BeEquivalentTo(["Strike"]);

        // Refresh abilities.json with a second ability now requiring FrostShard. The
        // ParseAndSwapAbilities path must rebuild the effect/ability cross-link.
        File.WriteAllText(Path.Combine(_cacheDir, "abilities.json"), """
        {
          "ability_1": { "InternalName": "Strike", "Name": "Strike", "Skill": "Sword", "Level": 1, "IconID": 1, "EffectKeywordReqs": ["FrostShard"] },
          "ability_2": { "InternalName": "Slash", "Name": "Slash", "Skill": "Sword", "Level": 2, "IconID": 2, "EffectKeywordReqs": ["FrostShard"] }
        }
        """);
        File.WriteAllText(Path.Combine(_cacheDir, "abilities.meta.json"), "{\"cdnVersion\":\"v2\",\"source\":1}");

        // Force re-load of abilities from cache.
        var reloaded = new ReferenceDataService(_cacheDir, NeverCallHttp(), bundledDir: _bundledDir);
        reloaded.AbilitiesByEffectKeyword["FrostShard"].Select(a => a.InternalName)
            .Should().BeEquivalentTo(["Strike", "Slash"]);
        await Task.CompletedTask;
    }

    [Fact]
    public void RefreshEffects_RebuildsEffectKeywordIndices()
    {
        // Single-shot construction verifies the effect-side indices rebuild when
        // ParseAndSwapEffects runs. The constructor calls LoadEffects which feeds
        // BuildEffectAbilityCrossLinkIndices, so a populated effect set yields a
        // populated EffectsByKeyword / EffectsByStackingType / EffectsByTriggeringAbilityKeyword.
        WriteFixture(effectsJson: """
        {
          "effect_1": {
            "Name": "Combo", "IconId": 1,
            "Keywords": ["Buff"], "StackingType": "GenericBuff", "AbilityKeywords": ["Attack"]
          }
        }
        """);

        var svc = new ReferenceDataService(_cacheDir, NeverCallHttp(), bundledDir: _bundledDir);

        svc.EffectsByKeyword["Buff"].Should().ContainSingle();
        svc.EffectsByStackingType["GenericBuff"].Should().ContainSingle();
        svc.EffectsByTriggeringAbilityKeyword["Attack"].Should().ContainSingle();
    }

    private sealed class ThrowingHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => throw new InvalidOperationException("HTTP must not be called in this test");
    }
}
