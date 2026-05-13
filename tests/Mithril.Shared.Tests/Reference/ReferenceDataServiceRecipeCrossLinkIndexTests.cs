using System.IO;
using System.Net.Http;
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
    public void KeywordDisplayNames_first_match_wins_when_a_keyword_has_multiple_singleton_slots()
    {
        // Two recipes both use Crystal as a singleton slot; the first one (recipe_100) sets a
        // friendly Desc, the second (recipe_200) sets the raw tag. First-match-wins should land
        // on the friendly Desc (recipe iteration order is dictionary order, which is stable enough
        // for the test fixture's controlled inputs).
        WriteFixture(
            itemsJson: """{ "item_100": { "Name": "Boots", "InternalName": "Boots" } }""",
            recipesJson: """
            {
              "recipe_100": {
                "Name": "R1", "InternalName": "R1", "Skill": "Smithing",
                "Ingredients": [
                  { "Desc": "Sparkly Crystal", "ItemKeys": ["Crystal"], "StackSize": 1 }
                ]
              },
              "recipe_200": {
                "Name": "R2", "InternalName": "R2", "Skill": "Smithing",
                "Ingredients": [
                  { "Desc": "Crystal", "ItemKeys": ["Crystal"], "StackSize": 1 }
                ]
              }
            }
            """);

        var svc = new ReferenceDataService(_cacheDir, NeverCallHttp(), bundledDir: _bundledDir);

        svc.KeywordDisplayNames.Should().ContainKey("Crystal")
            .WhoseValue.Should().Be("Sparkly Crystal",
                because: "first slot encountered with a friendly Desc claims the display name");
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
