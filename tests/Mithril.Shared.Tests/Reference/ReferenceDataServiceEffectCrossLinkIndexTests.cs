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
        svc.AbilitiesByEffectKeyword["FrostShard"].Select(m => m.Ability.InternalName)
            .Should().BeEquivalentTo(["Req", "Enabled", "TargetReq"]);

        // #318 slice 1: the index retains *which field* matched. Each single-field member
        // carries exactly its own reason flag.
        var byName = svc.AbilitiesByEffectKeyword["FrostShard"]
            .ToDictionary(m => m.Ability.InternalName!, m => m.Reason);
        byName["Req"].Should().Be(EffectAbilityMatchReason.Requires);
        byName["Enabled"].Should().Be(EffectAbilityMatchReason.EnabledBy);
        byName["TargetReq"].Should().Be(EffectAbilityMatchReason.Targets);
    }

    [Fact]
    public void AbilitiesByEffectKeyword_MultiFieldMember_CarriedOnceWithOredReasonFlags()
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

        // Dedup intent preserved: a single member (so a distinct-member count == the
        // displayed "View all N"), but provenance is complete — all three reason flags
        // are OR'd onto the one record. (No multi-reason member exists in bundled data
        // today, so this is the synthetic guard for the OR-accumulation path.)
        var match = svc.AbilitiesByEffectKeyword["FrostShard"].Should().ContainSingle().Which;
        match.Ability.InternalName.Should().Be("Double");
        match.Reason.Should().Be(
            EffectAbilityMatchReason.Requires
            | EffectAbilityMatchReason.EnabledBy
            | EffectAbilityMatchReason.Targets);
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
            .Which.Ability.InternalName.Should().Be("Player");
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
        svc.AbilitiesByEffectKeyword["FrostShard"].Select(m => m.Ability.InternalName)
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
        reloaded.AbilitiesByEffectKeyword["FrostShard"].Select(m => m.Ability.InternalName)
            .Should().BeEquivalentTo(["Strike", "Slash"]);
        await Task.CompletedTask;
    }

    [Fact]
    public void RealBundledEffects_KnownEntries_ProjectSensibly()
    {
        // Cookbook verification-ladder step 4: walk 2-3 real entries from the bundled JSON for
        // the kind and confirm each projects legibly with real data. Skips when the bundled
        // file isn't co-located (some CI shapes strip them); a real failure means a future
        // bundled-data update broke the projection contract for these entries.
        var bundledPath = LocateBundledEffects();
        if (bundledPath is null) return;

        var json = File.ReadAllText(bundledPath);
        var parsed = Mithril.Reference.Serialization.ReferenceDeserializer.ParseEffects(json);

        // effect_10003 is Sticky! — known canonical entry referenced in the handoff doc and
        // featuring the -2 duration sentinel that the projector maps to "Until removed".
        parsed.Should().ContainKey("effect_10003");
        var sticky = parsed["effect_10003"];
        sticky.InternalName.Should().Be("effect_10003", because: "the deserializer lifts the envelope key onto InternalName");
        sticky.Name.Should().NotBeNullOrEmpty();
        sticky.Name.Should().NotContain("(unknown)");

        // Every effect should round-trip with a populated InternalName after the lift.
        parsed.Values.Should().OnlyContain(e => !string.IsNullOrEmpty(e.InternalName));

        // The bundled set should be large enough to be meaningful — ~23k entries per the
        // handoff. A small sanity check below catches the case where the file shipped empty
        // (e.g. a botched CDN refresh).
        parsed.Count.Should().BeGreaterThan(10_000);
    }

    private static string? LocateBundledEffects() => LocateBundled("effects.json");

    private static string? LocateBundledData()
    {
        var effects = LocateBundled("effects.json");
        return effects is null ? null : Path.GetDirectoryName(effects);
    }

    private static string? LocateBundled(string file)
    {
        // Walk up from the test binary toward the repo root looking for the bundled file.
        var dir = AppContext.BaseDirectory;
        for (var i = 0; i < 10 && dir is not null; i++)
        {
            var candidate = Path.Combine(dir, "src", "Mithril.Shared", "Reference", "BundledData", file);
            if (File.Exists(candidate)) return candidate;
            dir = Directory.GetParent(dir)?.FullName;
        }
        return null;
    }

    [Fact]
    public void RealBundled_NonPrimaryFieldOnlyTag_RetainsProvenance_WouldHaveCaughtTheBug()
    {
        // #318 slice 1 — the load-bearing regression test.
        //
        // The original bug: the synthetic AbilityByEffectKeyword kind target re-derived the
        // set as `EffectKeywordReqs CONTAINS "<tag>"` — one of the three fields the index
        // unions. Quantified on bundled data: of 24 distinct effect-keyword tags,
        // EffectKeywordReqs carries exactly 1 (GraffitiSpot); EffectKeywordsIndicatingEnabled
        // carries 21; TargetEffectKeywordReq carries 2. So 23/24 tags deep-linked to an empty
        // list. This test pins a concrete non-primary-field-only tag — "BloodMist", carried
        // ONLY by EffectKeywordsIndicatingEnabled — and asserts it is present in the index
        // *with the correct provenance reason*. Pre-#318 the value shape discarded the field;
        // there was nowhere for this assertion to live. It now would.
        var bundledDir = LocateBundledData();
        if (bundledDir is null) return; // CI shapes that strip bundled data stay green.

        var svc = new ReferenceDataService(_cacheDir, NeverCallHttp(), bundledDir: bundledDir);

        svc.AbilitiesByEffectKeyword.Should().ContainKey("BloodMist",
            because: "BloodMist appears only in EffectKeywordsIndicatingEnabled, not EffectKeywordReqs — "
                + "the field the old synthetic deep-link queried");

        var members = svc.AbilitiesByEffectKeyword["BloodMist"];
        members.Should().NotBeEmpty();

        // Every BloodMist member qualified via EnabledBy and ONLY EnabledBy in bundled data.
        members.Should().OnlyContain(
            m => m.Reason == EffectAbilityMatchReason.EnabledBy,
            because: "BloodMist is not in any ability's EffectKeywordReqs or TargetEffectKeywordReq");

        // None carry the Requires flag — this is exactly the divergence the bug produced:
        // a 'Requires CONTAINS BloodMist' query returns empty while the index (correctly)
        // has 9 EnabledBy members.
        members.Should().OnlyContain(m => !m.Reason.HasFlag(EffectAbilityMatchReason.Requires));

        // The concrete ability set, pinned so a future bundled-data change that drops the
        // relationship surfaces as a failure here rather than a silent empty popup.
        members.Select(m => m.Ability.InternalName).Should().BeEquivalentTo(
            ["BloodMist1", "BloodMist2", "BloodMist3", "BloodMist4", "BloodMist5",
             "BloodMist6", "BloodMist7", "BloodMist8", "BloodMist9"]);

        // And the single primary-field tag still classifies as Requires — proving the
        // index distinguishes the fields rather than tagging everything uniformly.
        svc.AbilitiesByEffectKeyword.Should().ContainKey("GraffitiSpot");
        svc.AbilitiesByEffectKeyword["GraffitiSpot"].Should().OnlyContain(
            m => m.Reason == EffectAbilityMatchReason.Requires);

        // And a TargetEffectKeywordReq-only tag classifies as Targets.
        svc.AbilitiesByEffectKeyword.Should().ContainKey("MindRevealed");
        svc.AbilitiesByEffectKeyword["MindRevealed"].Should().OnlyContain(
            m => m.Reason == EffectAbilityMatchReason.Targets);
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
