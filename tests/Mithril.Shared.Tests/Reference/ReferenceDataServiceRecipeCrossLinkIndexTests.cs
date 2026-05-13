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
