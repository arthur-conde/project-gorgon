using System.IO;
using System.Net.Http;
using System.Text.RegularExpressions;
using FluentAssertions;
using Mithril.Reference.Models.Recipes;
using Mithril.Shared.Reference;
using Xunit;

namespace Mithril.Shared.Tests.Reference;

[Trait("Category", "FileIO")]
[Collection("FileIO")]
public sealed class ReferenceDataServiceRecipeCrossLinkIndexTests : IDisposable
{
    private readonly string _root;
    private readonly string _cacheDir;
    private readonly string _bundledDir;

    public ReferenceDataServiceRecipeCrossLinkIndexTests()
    {
        _root = Mithril.TestSupport.TestPaths.CreateTempDir("mithril-ref-crosslink-tests");
        _cacheDir = Path.Combine(_root, "cache");
        _bundledDir = Path.Combine(_root, "bundled");
        Directory.CreateDirectory(_cacheDir);
        Directory.CreateDirectory(_bundledDir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { }
    }

    private static HttpClient NeverCallHttp() =>
        new(new ThrowingHandler("HTTP must not be called in this test"));

    private void WriteFixture(string itemsJson, string recipesJson)
    {
        File.WriteAllText(Path.Combine(_bundledDir, "items.json"), itemsJson);
        File.WriteAllText(Path.Combine(_bundledDir, "items.meta.json"), "{\"cdnVersion\":\"v1\",\"source\":0}");
        File.WriteAllText(Path.Combine(_bundledDir, "recipes.json"), recipesJson);
        File.WriteAllText(Path.Combine(_bundledDir, "recipes.meta.json"), "{\"cdnVersion\":\"v1\",\"source\":0}");
    }

    [Fact]
    public void RecipesByProducedItem_IndexesByResultItemInternalName()
    {
        WriteFixture(
            itemsJson: """
            {
              "item_100": { "Name": "Tomato", "InternalName": "Tomato" },
              "item_101": { "Name": "Tomato Sauce", "InternalName": "TomatoSauce" }
            }
            """,
            recipesJson: """
            {
              "recipe_1": {
                "Name": "Make Tomato Sauce",
                "InternalName": "MakeTomatoSauce",
                "Skill": "Cooking",
                "Ingredients": [ { "ItemCode": 100, "StackSize": 3 } ],
                "ResultItems": [ { "ItemCode": 101, "StackSize": 1 } ]
              }
            }
            """);

        var svc = new ReferenceDataService(_cacheDir, NeverCallHttp(), bundledDir: _bundledDir);

        svc.RecipesByProducedItem.Should().ContainKey("TomatoSauce");
        svc.RecipesByProducedItem["TomatoSauce"].Should().ContainSingle()
            .Which.InternalName.Should().Be("MakeTomatoSauce");
    }

    [Fact]
    public void RecipesByProducedItem_FallsBackToProtoResultItems_WhenResultItemsEmpty()
    {
        WriteFixture(
            itemsJson: """
            {
              "item_200": { "Name": "Boots", "InternalName": "Boots" }
            }
            """,
            recipesJson: """
            {
              "recipe_proto": {
                "Name": "Craft Boots",
                "InternalName": "CraftBoots",
                "Skill": "Leatherworking",
                "Ingredients": [],
                "ProtoResultItems": [ { "ItemCode": 200, "StackSize": 1 } ]
              }
            }
            """);

        var svc = new ReferenceDataService(_cacheDir, NeverCallHttp(), bundledDir: _bundledDir);

        svc.RecipesByProducedItem.Should().ContainKey("Boots");
        svc.RecipesByProducedItem["Boots"].Should().ContainSingle()
            .Which.InternalName.Should().Be("CraftBoots");
    }

    [Fact]
    public void RecipesByIngredientItem_IndexesItemIngredientsByInternalName()
    {
        WriteFixture(
            itemsJson: """
            {
              "item_100": { "Name": "Tomato", "InternalName": "Tomato" },
              "item_101": { "Name": "Salt", "InternalName": "Salt" }
            }
            """,
            recipesJson: """
            {
              "recipe_1": {
                "Name": "Salted Tomato",
                "InternalName": "SaltedTomato",
                "Skill": "Cooking",
                "Ingredients": [
                  { "ItemCode": 100, "StackSize": 1 },
                  { "ItemCode": 101, "StackSize": 1 }
                ]
              }
            }
            """);

        var svc = new ReferenceDataService(_cacheDir, NeverCallHttp(), bundledDir: _bundledDir);

        svc.RecipesByIngredientItem.Should().ContainKey("Tomato");
        svc.RecipesByIngredientItem.Should().ContainKey("Salt");
        svc.RecipesByIngredientItem["Tomato"].Should().ContainSingle()
            .Which.InternalName.Should().Be("SaltedTomato");
    }

    [Fact]
    public void RecipesByIngredientItem_DoesNotIndexKeywordIngredients()
    {
        // Keyword ingredients are kind-based (e.g. any "Crystal") and don't map to a single
        // InternalName. Surfacing them as item-keyed entries would flood the reverse index;
        // they live on KeywordsUsedInRecipeSlots instead.
        WriteFixture(
            itemsJson: """
            {
              "item_100": { "Name": "Boots", "InternalName": "Boots" },
              "item_200": { "Name": "Rough Crystal", "InternalName": "RoughCrystal", "Keywords": ["Crystal"] }
            }
            """,
            recipesJson: """
            {
              "recipe_1": {
                "Name": "Enchant Boots",
                "InternalName": "EnchantBoots",
                "Skill": "Leatherworking",
                "Ingredients": [
                  { "ItemCode": 100, "StackSize": 1 },
                  { "Desc": "Aux Crystal", "ItemKeys": ["Crystal"], "StackSize": 1 }
                ]
              }
            }
            """);

        var svc = new ReferenceDataService(_cacheDir, NeverCallHttp(), bundledDir: _bundledDir);

        svc.RecipesByIngredientItem.Should().ContainKey("Boots");
        svc.RecipesByIngredientItem.Should().NotContainKey("RoughCrystal",
            because: "keyword-matched usage lives in KeywordsUsedInRecipeSlots, not the per-item dictionary");
    }

    [Fact]
    public void RecipesByIngredientItemWithReason_membership_equals_RecipesByIngredientItem_singleReason()
    {
        // #318 slice 4, surface 1 — Gate C (reference-layer half). The provenance-retaining
        // index must be the SAME set as RecipesByIngredientItem (single materialization —
        // the #318 invariant: the popup is a view over the index, never a re-derivation),
        // and because the relationship is single-reason every member carries exactly the
        // DirectIngredient flag. A regression that let the two indices drift (the original
        // dual-derivation bug class) fails here.
        WriteFixture(
            itemsJson: """
            {
              "item_100": { "Name": "Tomato", "InternalName": "Tomato" },
              "item_101": { "Name": "Salt", "InternalName": "Salt" }
            }
            """,
            recipesJson: """
            {
              "recipe_1": {
                "Name": "Salted Tomato", "InternalName": "SaltedTomato", "Skill": "Cooking",
                "Ingredients": [
                  { "ItemCode": 100, "StackSize": 1 },
                  { "ItemCode": 101, "StackSize": 1 }
                ]
              },
              "recipe_2": {
                "Name": "Tomato Soup", "InternalName": "TomatoSoup", "Skill": "Cooking",
                "Ingredients": [ { "ItemCode": 100, "StackSize": 2 } ]
              }
            }
            """);

        var svc = new ReferenceDataService(_cacheDir, NeverCallHttp(), bundledDir: _bundledDir);

        // Same key set.
        svc.RecipesByIngredientItemWithReason.Keys.Should().BeEquivalentTo(
            svc.RecipesByIngredientItem.Keys);

        foreach (var (item, plain) in svc.RecipesByIngredientItem)
        {
            var withReason = svc.RecipesByIngredientItemWithReason[item];
            // Same members (same Recipe instances), same order.
            withReason.Select(m => m.Recipe).Should().Equal(plain,
                because: "the provenance index is a view over the same materialized set");
            // Single-reason: every member is exactly DirectIngredient (a distinct-member
            // count therefore equals the displayed 'View all N').
            withReason.Should().OnlyContain(
                m => m.Reason == RecipeIngredientItemMatchReason.DirectIngredient);
        }

        // Tomato is referenced by two distinct recipes — both carried, once each.
        svc.RecipesByIngredientItemWithReason["Tomato"]
            .Select(m => m.Recipe.InternalName)
            .Should().BeEquivalentTo(new[] { "SaltedTomato", "TomatoSoup" });
    }

    [Fact]
    public void RecipesByIngredientItemWithReason_recipeQualifyingViaNonFirstSlot_stillCarriedOnce_withDirectIngredient()
    {
        // #318 Gate C — the load-bearing regression. A recipe that consumes the item via a
        // *non-primary* slot (here: the SECOND ingredient slot, the first being a keyword
        // slot that never feeds this index) must still appear, once, with DirectIngredient
        // provenance. The original dual-derivation bug surfaced exactly when a member
        // qualified via something other than the field the re-derived query inspected; the
        // popup-from-index has no query, so the member is present by construction.
        WriteFixture(
            itemsJson: """
            {
              "item_100": { "Name": "Rough Crystal", "InternalName": "RoughCrystal", "Keywords": ["Crystal"] },
              "item_200": { "Name": "Iron Bar", "InternalName": "IronBar" }
            }
            """,
            recipesJson: """
            {
              "recipe_1": {
                "Name": "Enchant Bar", "InternalName": "EnchantBar", "Skill": "Smithing",
                "Ingredients": [
                  { "Desc": "Aux Crystal", "ItemKeys": ["Crystal"], "StackSize": 1 },
                  { "ItemCode": 200, "StackSize": 1 }
                ]
              },
              "recipe_2": {
                "Name": "Reforge Bar", "InternalName": "ReforgeBar", "Skill": "Smithing",
                "Ingredients": [
                  { "ItemCode": 200, "StackSize": 1 },
                  { "ItemCode": 200, "StackSize": 1 }
                ]
              }
            }
            """);

        var svc = new ReferenceDataService(_cacheDir, NeverCallHttp(), bundledDir: _bundledDir);

        // IronBar qualifies in recipe_1 only via the second slot, and in recipe_2 via two
        // direct slots — it must appear once per recipe (deduped), with DirectIngredient.
        svc.RecipesByIngredientItemWithReason.Should().ContainKey("IronBar");
        var members = svc.RecipesByIngredientItemWithReason["IronBar"];
        members.Select(m => m.Recipe.InternalName).Should().BeEquivalentTo(
            new[] { "EnchantBar", "ReforgeBar" },
            because: "membership is by recipe, deduped, regardless of which slot matched");
        members.Should().OnlyContain(m => m.Reason == RecipeIngredientItemMatchReason.DirectIngredient);

        // The keyword-only item is NOT in the per-item provenance index (it's the 'Used as'
        // surface, #259 — out of scope here and never feeding this index).
        svc.RecipesByIngredientItemWithReason.Should().NotContainKey("RoughCrystal");
    }

    [Fact]
    public void KeywordsUsedInRecipeSlots_collects_distinct_tags_across_all_RecipeKeywordIngredient_slots()
    {
        WriteFixture(
            itemsJson: """
            { "item_100": { "Name": "Boots", "InternalName": "Boots" } }
            """,
            recipesJson: """
            {
              "recipe_singleton": {
                "Name": "R1", "InternalName": "R1", "Skill": "Leatherworking",
                "Ingredients": [
                  { "ItemCode": 100, "StackSize": 1 },
                  { "Desc": "Aux Crystal", "ItemKeys": ["Crystal"], "StackSize": 1 }
                ]
              },
              "recipe_tuple": {
                "Name": "R2", "InternalName": "R2", "Skill": "Leatherworking",
                "Ingredients": [
                  { "Desc": "T2", "ItemKeys": ["Crystal", "Tier2"], "StackSize": 1 }
                ]
              }
            }
            """);

        var svc = new ReferenceDataService(_cacheDir, NeverCallHttp(), bundledDir: _bundledDir);

        svc.KeywordsUsedInRecipeSlots.Should().BeEquivalentTo(["Crystal", "Tier2"]);
    }

    [Fact]
    public void KeywordsUsedInRecipeSlots_is_empty_when_no_recipes_reference_keyword_slots()
    {
        WriteFixture(
            itemsJson: """{ "item_100": { "Name": "X", "InternalName": "X" } }""",
            recipesJson: """
            {
              "recipe_1": {
                "Name": "R1", "InternalName": "R1", "Skill": "Cooking",
                "Ingredients": [ { "ItemCode": 100, "StackSize": 1 } ]
              }
            }
            """);

        var svc = new ReferenceDataService(_cacheDir, NeverCallHttp(), bundledDir: _bundledDir);

        svc.KeywordsUsedInRecipeSlots.Should().BeEmpty();
    }

    [Fact]
    public void RecipesByIngredientKeywordWithReason_membership_tracksKeywordSlots_singleReason_dedupedByRecipe()
    {
        // #318 slice 4, surface 2 — Gate C (reference-layer half). The provenance-retaining
        // "Used as" index must be derived from the SAME keyword-slot walk that builds
        // KeywordsUsedInRecipeSlots (single materialization — the #318 invariant: the
        // popup is a view over the index, never a re-derivation), and because the
        // relationship is single-reason every member carries exactly the
        // KeywordIngredientSlot flag. A regression that let the keyword set and the
        // reverse index drift (the original dual-derivation bug class) fails here. A
        // recipe listing the SAME tag in several keyword slots is carried once.
        WriteFixture(
            itemsJson: """
            { "item_100": { "Name": "Filler", "InternalName": "Filler" } }
            """,
            recipesJson: """
            {
              "recipe_1": {
                "Name": "Enchant Ring", "InternalName": "EnchantRing", "Skill": "Enchanting",
                "Ingredients": [
                  { "Desc": "Aux Crystal", "ItemKeys": ["Crystal"], "StackSize": 1 },
                  { "Desc": "Second Crystal", "ItemKeys": ["Crystal"], "StackSize": 1 }
                ]
              },
              "recipe_2": {
                "Name": "Cut Gem", "InternalName": "CutGem", "Skill": "Jewelry",
                "Ingredients": [
                  { "Desc": "Any Gem", "ItemKeys": ["Gem"], "StackSize": 1 }
                ]
              },
              "recipe_3": {
                "Name": "Fuse Gemstone", "InternalName": "FuseGemstone", "Skill": "Jewelry",
                "Ingredients": [
                  { "Desc": "A Crystal", "ItemKeys": ["Crystal"], "StackSize": 1 },
                  { "Desc": "A Gem", "ItemKeys": ["Gem"], "StackSize": 1 }
                ]
              }
            }
            """);

        var svc = new ReferenceDataService(_cacheDir, NeverCallHttp(), bundledDir: _bundledDir);

        // Same tag set as KeywordsUsedInRecipeSlots (single materialization — cannot drift).
        svc.RecipesByIngredientKeywordWithReason.Keys.Should().BeEquivalentTo(
            svc.KeywordsUsedInRecipeSlots);

        // Crystal: recipe_1 (two Crystal slots → carried ONCE) + recipe_3.
        var crystal = svc.RecipesByIngredientKeywordWithReason["Crystal"];
        crystal.Select(m => m.Recipe.InternalName).Should().BeEquivalentTo(
            new[] { "EnchantRing", "FuseGemstone" },
            because: "a recipe listing the tag in several keyword slots is one member");
        crystal.Should().OnlyContain(
            m => m.Reason == RecipeIngredientKeywordMatchReason.KeywordIngredientSlot);

        // Gem: recipe_2 + recipe_3.
        svc.RecipesByIngredientKeywordWithReason["Gem"]
            .Select(m => m.Recipe.InternalName)
            .Should().BeEquivalentTo(new[] { "CutGem", "FuseGemstone" });
        svc.RecipesByIngredientKeywordWithReason["Gem"].Should().OnlyContain(
            m => m.Reason == RecipeIngredientKeywordMatchReason.KeywordIngredientSlot);
    }

    [Fact]
    public void RecipesByIngredientKeywordWithReason_recipeQualifyingViaNonFirstSlot_stillCarriedOnce()
    {
        // #318 Gate C — the load-bearing regression. A recipe that references the tag via
        // a *non-primary* slot (here: the SECOND ingredient slot; the first being a direct
        // item slot that feeds the separate "Used in" index) must still appear, once, with
        // KeywordIngredientSlot provenance. The original dual-derivation bug surfaced
        // exactly when a member qualified via something other than what a re-derived query
        // inspected; the popup-from-index has no query, so the member is present by
        // construction.
        WriteFixture(
            itemsJson: """
            { "item_200": { "Name": "Iron Bar", "InternalName": "IronBar" } }
            """,
            recipesJson: """
            {
              "recipe_1": {
                "Name": "Forge Blade", "InternalName": "ForgeBlade", "Skill": "Smithing",
                "Ingredients": [
                  { "ItemCode": 200, "StackSize": 2 },
                  { "Desc": "Aux Crystal", "ItemKeys": ["Crystal"], "StackSize": 1 }
                ]
              }
            }
            """);

        var svc = new ReferenceDataService(_cacheDir, NeverCallHttp(), bundledDir: _bundledDir);

        // The recipe qualifies for "Crystal" only via its SECOND (keyword) slot.
        svc.RecipesByIngredientKeywordWithReason.Should().ContainKey("Crystal");
        var members = svc.RecipesByIngredientKeywordWithReason["Crystal"];
        members.Select(m => m.Recipe.InternalName).Should().BeEquivalentTo(
            new[] { "ForgeBlade" },
            because: "membership is by keyword slot regardless of slot position");
        members.Should().OnlyContain(
            m => m.Reason == RecipeIngredientKeywordMatchReason.KeywordIngredientSlot);

        // The direct item slot does NOT leak into the keyword index (it's the separate
        // "Used in" surface, RecipesByIngredientItemWithReason).
        svc.RecipesByIngredientKeywordWithReason.Should().NotContainKey("IronBar");
    }

    [Fact]
    public void KeywordDisplayNames_uses_recipes_json_Desc_when_strings_all_absent()
    {
        WriteFixture(
            itemsJson: """{ "item_100": { "Name": "Boots", "InternalName": "Boots" } }""",
            recipesJson: """
            {
              "recipe_777": {
                "Name": "R", "InternalName": "R", "Skill": "Smithing",
                "Ingredients": [
                  { "Desc": "Metal Armor", "ItemKeys": ["MetalArmor"], "StackSize": 1 }
                ]
              }
            }
            """);

        var svc = new ReferenceDataService(_cacheDir, NeverCallHttp(), bundledDir: _bundledDir);

        svc.KeywordDisplayNames.Should().ContainKey("MetalArmor")
            .WhoseValue.Should().Be("Metal Armor",
                because: "the slot is a singleton and its recipes.json Desc is a friendly form of the raw tag");
    }

    [Fact]
    public void KeywordDisplayNames_prefers_strings_all_over_recipes_json_Desc()
    {
        File.WriteAllText(Path.Combine(_bundledDir, "items.json"),
            """{ "item_100": { "Name": "Boots", "InternalName": "Boots" } }""");
        File.WriteAllText(Path.Combine(_bundledDir, "items.meta.json"), "{\"cdnVersion\":\"v1\",\"source\":0}");
        File.WriteAllText(Path.Combine(_bundledDir, "recipes.json"),
            """
            {
              "recipe_888": {
                "Name": "R", "InternalName": "R", "Skill": "Smithing",
                "Ingredients": [
                  { "Desc": "Main-Hand Weapon", "ItemKeys": ["MainHandWeapon"], "StackSize": 1 }
                ]
              }
            }
            """);
        File.WriteAllText(Path.Combine(_bundledDir, "recipes.meta.json"), "{\"cdnVersion\":\"v1\",\"source\":0}");
        File.WriteAllText(Path.Combine(_bundledDir, "strings_all.json"),
            """{ "recipe_888_Ingredients_0_Desc": "Main-Hand Item" }""");
        File.WriteAllText(Path.Combine(_bundledDir, "strings_all.meta.json"), "{\"cdnVersion\":\"v1\",\"source\":0}");

        var svc = new ReferenceDataService(_cacheDir, NeverCallHttp(), bundledDir: _bundledDir);

        svc.KeywordDisplayNames.Should().ContainKey("MainHandWeapon")
            .WhoseValue.Should().Be("Main-Hand Item",
                because: "strings_all takes precedence over recipes.json's Desc — it's the in-game wording");
    }

    [Fact]
    public void KeywordDisplayNames_omits_keywords_whose_Desc_is_the_raw_tag()
    {
        // recipe_9008's GreenCrystal slot has Desc = "GreenCrystal" — same as the raw tag.
        // The map should NOT contain GreenCrystal so the caller knows to apply a CamelCaseSplit fallback.
        WriteFixture(
            itemsJson: """{ "item_100": { "Name": "Boots", "InternalName": "Boots" } }""",
            recipesJson: """
            {
              "recipe_9008": {
                "Name": "R", "InternalName": "R", "Skill": "Teleportation",
                "Ingredients": [
                  { "Desc": "GreenCrystal", "ItemKeys": ["GreenCrystal"], "StackSize": 1 }
                ]
              }
            }
            """);

        var svc = new ReferenceDataService(_cacheDir, NeverCallHttp(), bundledDir: _bundledDir);

        svc.KeywordsUsedInRecipeSlots.Should().Contain("GreenCrystal");
        svc.KeywordDisplayNames.Should().NotContainKey("GreenCrystal",
            because: "a Desc identical to the raw tag adds no value over a CamelCase-split fallback");
    }

    [Fact]
    public void KeywordDisplayNames_ignores_composite_tuple_slots()
    {
        // Composite slots like ["EquipmentSlot:MainHand", "MinTSysPrereq:0"] have Descs that
        // describe the AND-matched composite ("Main-Hand Item"), not any single keyword tag.
        // Surfacing the composite's Desc for either tag would be misleading.
        WriteFixture(
            itemsJson: """{ "item_100": { "Name": "Boots", "InternalName": "Boots" } }""",
            recipesJson: """
            {
              "recipe_555": {
                "Name": "R", "InternalName": "R", "Skill": "Augmentation",
                "Ingredients": [
                  { "Desc": "Main-Hand Item", "ItemKeys": ["EquipmentSlot:MainHand", "MinTSysPrereq:0"], "StackSize": 1 }
                ]
              }
            }
            """);

        var svc = new ReferenceDataService(_cacheDir, NeverCallHttp(), bundledDir: _bundledDir);

        svc.KeywordDisplayNames.Should().NotContainKey("EquipmentSlot:MainHand");
        svc.KeywordDisplayNames.Should().NotContainKey("MinTSysPrereq:0");
    }

    [Fact]
    public void KeywordDisplayNames_most_common_Desc_wins_across_singleton_slots()
    {
        // Three recipes use CheapMeat as a singleton slot with two distinct friendly Descs.
        // The dominant Desc ("Cheap Meat", 2 occurrences) should be picked over the variant
        // ("Cheap Meat (stack of 25)", 1 occurrence) — mirroring the bundled-data shape.
        WriteFixture(
            itemsJson: """{ "item_100": { "Name": "Boots", "InternalName": "Boots" } }""",
            recipesJson: """
            {
              "recipe_100": {
                "Name": "R1", "InternalName": "R1", "Skill": "Cooking",
                "Ingredients": [
                  { "Desc": "Cheap Meat (stack of 25)", "ItemKeys": ["CheapMeat"], "StackSize": 25 }
                ]
              },
              "recipe_200": {
                "Name": "R2", "InternalName": "R2", "Skill": "Cooking",
                "Ingredients": [
                  { "Desc": "Cheap Meat", "ItemKeys": ["CheapMeat"], "StackSize": 1 }
                ]
              },
              "recipe_300": {
                "Name": "R3", "InternalName": "R3", "Skill": "Cooking",
                "Ingredients": [
                  { "Desc": "Cheap Meat", "ItemKeys": ["CheapMeat"], "StackSize": 1 }
                ]
              }
            }
            """);

        var svc = new ReferenceDataService(_cacheDir, NeverCallHttp(), bundledDir: _bundledDir);

        svc.KeywordDisplayNames.Should().ContainKey("CheapMeat")
            .WhoseValue.Should().Be("Cheap Meat",
                because: "the most-common Desc across singleton slots wins (2 vs 1)");
    }

    [Fact]
    public void KeywordDisplayNames_override_suppresses_lookup_to_force_consumer_fallback()
    {
        // The Crystal keyword's singleton slots are labelled by their slot ROLE ("Primary Crystal",
        // "Auxiliary Crystal") rather than describing what a Crystal is. The override table maps
        // Crystal to null, which suppresses the slot-walk for this tag — the consumer's
        // CamelCaseSplit fallback then yields the raw "Crystal" instead of a misleading slot label.
        WriteFixture(
            itemsJson: """{ "item_100": { "Name": "Boots", "InternalName": "Boots" } }""",
            recipesJson: """
            {
              "recipe_100": {
                "Name": "R1", "InternalName": "R1", "Skill": "Smithing",
                "Ingredients": [
                  { "Desc": "Primary Crystal", "ItemKeys": ["Crystal"], "StackSize": 1 }
                ]
              },
              "recipe_200": {
                "Name": "R2", "InternalName": "R2", "Skill": "Smithing",
                "Ingredients": [
                  { "Desc": "Auxiliary Crystal", "ItemKeys": ["Crystal"], "StackSize": 1 }
                ]
              }
            }
            """);

        var svc = new ReferenceDataService(_cacheDir, NeverCallHttp(), bundledDir: _bundledDir);

        svc.KeywordDisplayNames.Should().NotContainKey("Crystal",
            because: "Crystal is in the suppression override — let the consumer's fallback handle it");
    }

    [Fact]
    public void KeywordDisplayNames_real_bundled_data_no_unreviewed_divergent_display_names()
    {
        // Drift detector. Walks the picked display names for the real bundled recipes.json +
        // strings_all.json and verifies each shares at least one length-≥3 token with the
        // camel-case-split keyword tag. When this fails, the slot-Desc resolution has produced
        // a label that's recipe-context-specific (the "Item to Copy Appearance From" pattern that
        // bit us on the Equipment keyword). Each new failure means either:
        //   - The keyword belongs in ReferenceDataService.KeywordDisplayOverrides (null to
        //     suppress, or a friendly value to pin).
        //   - The divergence is acceptable game-side wording (e.g. "Trophy Skin or Hide" for
        //     FlawlessSkin), in which case allowlist it below.
        var realBundled = Path.Combine(AppContext.BaseDirectory, "Reference", "BundledData");
        if (!File.Exists(Path.Combine(realBundled, "recipes.json"))) return;

        var svc = new ReferenceDataService(_cacheDir, NeverCallHttp(), bundledDir: realBundled);

        // Allowlist for cases where the divergent name is the right in-game wording. Audited
        // 2026-05-14. Re-audit any time this list grows.
        var allowlistExact = new HashSet<string>(StringComparer.Ordinal)
        {
            "FlawlessSkin",     // → "Trophy Skin or Hide" — the dominant in-game labelling.
            "MainHandAugment",  // → "Weapon Augment" — dominant in-game labelling.
            "EnergyBow",        // → "Faebow" — two recipes deliberately label by the weapon class.
            "Edible",           // → "Food or Drink" — both edible-keyword recipes label it this way.
            "VendorTrash",      // → "Miscellaneous Junk" — natural in-game term.
            "NecroFuel",        // → "Any Decent-Sized Bone" — natural enumeration.
        };
        // Pattern allowlist. Brewing*<Level> tags (e.g. BrewingGarnishA2, BrewingFlowersW6) are
        // opaque shorthand for kind-keyed slots; the slot Descs enumerate the actual accepted
        // items ("Beet, Squash, Broccoli, or Carrot") which is the right in-game wording.
        bool IsAllowed(string tag) =>
            allowlistExact.Contains(tag) || tag.StartsWith("Brewing", StringComparison.Ordinal);

        // Audit scope: only keywords that could actually surface as a chip — i.e. those present
        // in some raw item.Keywords list. Synthesized/virtual keywords (e.g. EquipmentSlot:Hands)
        // appear in recipe slots but never on items directly, so a divergent display name there
        // doesn't reach the UI.
        var chipSurfacingKeywords = new HashSet<string>(StringComparer.Ordinal);
        foreach (var item in svc.Items.Values)
        {
            if (item.Keywords is null) continue;
            foreach (var kw in item.Keywords)
                chipSurfacingKeywords.Add(kw.Tag);
        }

        var tokenSplitter = new Regex("[A-Z][a-z0-9]*|[a-z0-9]+", RegexOptions.Compiled);
        var failures = new List<string>();
        foreach (var (tag, display) in svc.KeywordDisplayNames)
        {
            if (!chipSurfacingKeywords.Contains(tag)) continue;
            if (IsAllowed(tag)) continue;

            HashSet<string> Tokens(string s) =>
                tokenSplitter.Matches(s).Select(m => m.Value.ToLowerInvariant())
                    .Where(t => t.Length >= 3)
                    .ToHashSet(StringComparer.Ordinal);

            var tagTokens = Tokens(tag);
            var displayTokens = Tokens(display);
            if (tagTokens.Count == 0 || displayTokens.Count == 0) continue;
            if (!tagTokens.Overlaps(displayTokens))
                failures.Add($"'{tag}' → '{display}' (no shared length-≥3 token)");
        }

        if (failures.Count > 0)
        {
            var lines = string.Join(Environment.NewLine, failures.Select(f => "  • " + f));
            throw new Xunit.Sdk.XunitException(
                $"Keyword display names diverge from their tags without a corresponding override:{Environment.NewLine}{lines}{Environment.NewLine}" +
                "Each entry is a chip-surfacing keyword whose resolved Desc shares no length-≥3 token with the camel-case-split tag. " +
                "Add to ReferenceDataService.KeywordDisplayOverrides (null to suppress, or pin a value), " +
                "or extend this test's allowlist if the wording is intentional.");
        }
    }

    [Fact]
    public void Indices_SkipItemsThatLackInternalName()
    {
        // Some items in the bundled file lack InternalName; cross-link indices key on
        // InternalName so those entries must be silently skipped rather than crashing.
        WriteFixture(
            itemsJson: """
            {
              "item_100": { "Name": "Anonymous" }
            }
            """,
            recipesJson: """
            {
              "recipe_1": {
                "Name": "X",
                "InternalName": "X",
                "Skill": "Cooking",
                "Ingredients": [ { "ItemCode": 100, "StackSize": 1 } ],
                "ResultItems": [ { "ItemCode": 100, "StackSize": 1 } ]
              }
            }
            """);

        var svc = new ReferenceDataService(_cacheDir, NeverCallHttp(), bundledDir: _bundledDir);

        svc.RecipesByProducedItem.Should().BeEmpty();
        svc.RecipesByIngredientItem.Should().BeEmpty();
    }

    private sealed class ThrowingHandler(string message) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => throw new InvalidOperationException(message);
    }
}
