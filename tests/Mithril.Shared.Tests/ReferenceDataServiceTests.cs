using System.IO;
using System.Net;
using System.Net.Http;
using FluentAssertions;
using Mithril.Shared.Reference;
using Xunit;

namespace Mithril.Shared.Tests;

[Trait("Category", "FileIO")]
[Collection("FileIO")]
public class ReferenceDataServiceTests : IDisposable
{
    private readonly string _root;
    private readonly string _cacheDir;
    private readonly string _bundledDir;

    public ReferenceDataServiceTests()
    {
        _root = Mithril.TestSupport.TestPaths.CreateTempDir("mithril-ref-tests");
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

    [Fact]
    public void LoadsBundledFallback_WhenCacheMissing()
    {
        WriteBundled("""
            { "item_42": { "Name": "Test Seeds", "InternalName": "TestSeeds", "MaxStackSize": 50, "IconId": 7 } }
            """, version: "v100");

        var svc = new ReferenceDataService(_cacheDir, NeverCallHttp(), bundledDir: _bundledDir);

        svc.Items.Should().ContainKey(42L);
        svc.Items[42L].InternalName.Should().Be("TestSeeds");
        svc.ItemsByInternalName["TestSeeds"].Id.Should().Be(42);
        var snap = svc.GetSnapshot("items");
        snap.Source.Should().Be(ReferenceFileSource.Bundled);
        snap.CdnVersion.Should().Be("v100");
        snap.EntryCount.Should().Be(1);
    }

    [Fact]
    public void PrefersCacheOverBundled_WhenCachePresent()
    {
        WriteBundled("""{ "item_1": { "Name": "Old", "InternalName": "OldSeeds" } }""", version: "v100");
        WriteCache("""{ "item_2": { "Name": "Newer", "InternalName": "NewerSeeds" } }""", version: "v200");

        var svc = new ReferenceDataService(_cacheDir, NeverCallHttp(), bundledDir: _bundledDir);

        svc.Items.Should().ContainKey(2L).And.NotContainKey(1L);
        svc.GetSnapshot("items").Source.Should().Be(ReferenceFileSource.Cache);
        svc.GetSnapshot("items").CdnVersion.Should().Be("v200");
    }

    [Fact]
    public async Task RefreshAsync_FetchesCdn_OverwritesCacheAndRaisesUpdated()
    {
        WriteBundled("""{ "item_1": { "Name": "Bundled", "InternalName": "BundledSeeds" } }""", version: "v100");

        var rootHtml = """<html><meta http-equiv="refresh" content="2; URL=/v500/data/index.html"></html>""";
        var freshJson = """{ "item_99": { "Name": "Fresh", "InternalName": "FreshSeeds", "MaxStackSize": 10, "IconId": 5 } }""";
        var handler = new RoutingHandler(req =>
        {
            var path = req.RequestUri!.AbsolutePath;
            if (path is "/" or "")
                return Respond(rootHtml, "text/html");
            if (path == "/v500/data/items.json")
                return Respond(freshJson, "application/json");
            return new HttpResponseMessage(HttpStatusCode.NotFound);
        });

        var svc = new ReferenceDataService(_cacheDir, new HttpClient(handler), bundledDir: _bundledDir);
        var raisedFor = new List<string>();
        svc.FileUpdated += (_, key) => raisedFor.Add(key);

        await svc.RefreshAsync("items");

        svc.Items.Should().ContainKey(99L).And.NotContainKey(1L);
        svc.GetSnapshot("items").Source.Should().Be(ReferenceFileSource.Cdn);
        svc.GetSnapshot("items").CdnVersion.Should().Be("v500");
        var itemsPath = Path.Combine(_cacheDir, "items.json");
        var itemsMetaPath = Path.Combine(_cacheDir, "items.meta.json");
        var dirContents = Directory.Exists(_cacheDir)
            ? string.Join(",", Directory.GetFileSystemEntries(_cacheDir))
            : "<dir missing>";
        File.Exists(itemsPath).Should().BeTrue(because: $"wrote to {itemsPath}; cacheDir contents: [{dirContents}]");
        File.Exists(itemsMetaPath).Should().BeTrue(because: $"wrote to {itemsMetaPath}; cacheDir contents: [{dirContents}]");
        raisedFor.Should().Equal("items");
    }

    [Fact]
    public async Task FailedRefresh_KeepsExistingData()
    {
        WriteBundled("""{ "item_1": { "Name": "Bundled", "InternalName": "BundledSeeds" } }""", version: "v100");

        var handler = new RoutingHandler(_ => new HttpResponseMessage(HttpStatusCode.InternalServerError));
        var svc = new ReferenceDataService(_cacheDir, new HttpClient(handler), bundledDir: _bundledDir);

        await svc.RefreshAsync("items");

        svc.Items.Should().ContainKey(1L);
        svc.GetSnapshot("items").Source.Should().Be(ReferenceFileSource.Bundled);
    }

    [Fact]
    public void Keys_ContainsItems()
    {
        WriteBundled("""{}""", version: "v100");
        var svc = new ReferenceDataService(_cacheDir, NeverCallHttp(), bundledDir: _bundledDir);
        svc.Keys.Should().Contain("items");
    }

    [Fact]
    public void RealBundledFile_ParsesAndContainsKnownSeeds()
    {
        // Validate the real ~6.8 MB bundled items.json deserializes and the
        // canonical Barley/Pansy seeds are reachable by InternalName.
        var realBundled = Path.Combine(AppContext.BaseDirectory, "Reference", "BundledData");
        if (!File.Exists(Path.Combine(realBundled, "items.json")))
            return; // Bundled file may be absent in some test runners; skip rather than fail.

        var svc = new ReferenceDataService(_cacheDir, NeverCallHttp(), bundledDir: realBundled);

        svc.Items.Count.Should().BeGreaterThan(1000);
        svc.ItemsByInternalName.Should().ContainKey("BarleySeeds");
        svc.ItemsByInternalName["BarleySeeds"].Name.Should().Be("Barley Seeds");
        svc.ItemsByInternalName.Should().ContainKey("FlowerSeeds6");
        svc.ItemsByInternalName["FlowerSeeds6"].Name.Should().Be("Pansy Seeds");
    }

    [Fact]
    public void Recipe_ingredients_with_ItemKeys_parse_as_keyword_ingredients()
    {
        // items.json drives keyword index population (items must be parsed for the catalog
        // to know which items carry "Crystal"); recipes.json carries the keyword-matched slot.
        File.WriteAllText(Path.Combine(_bundledDir, "items.json"), """
            {
              "item_100": { "Name": "Boots", "InternalName": "Boots", "MaxStackSize": 1 },
              "item_200": { "Name": "Rough Crystal", "InternalName": "RoughCrystal", "MaxStackSize": 50, "Keywords": ["Crystal"] }
            }
            """);
        File.WriteAllText(Path.Combine(_bundledDir, "items.meta.json"), "{\"cdnVersion\":\"v1\",\"source\":0}");
        File.WriteAllText(Path.Combine(_bundledDir, "recipes.json"), """
            {
              "recipe_1": {
                "Name": "EnchantBoots",
                "InternalName": "EnchantBoots",
                "Skill": "Leatherworking",
                "Ingredients": [
                  { "ItemCode": 100, "StackSize": 1 },
                  { "Desc": "Auxiliary Crystal", "ItemKeys": ["Crystal"], "StackSize": 1 }
                ],
                "ResultItems": [
                  { "ItemCode": 100, "StackSize": 1 }
                ]
              }
            }
            """);
        File.WriteAllText(Path.Combine(_bundledDir, "recipes.meta.json"), "{\"cdnVersion\":\"v1\",\"source\":0}");

        var svc = new ReferenceDataService(_cacheDir, NeverCallHttp(), bundledDir: _bundledDir);

        svc.Recipes.Should().ContainKey("recipe_1");
        var recipe = svc.Recipes["recipe_1"];
        recipe.Ingredients.Should().HaveCount(2);

        recipe.Ingredients[0].Should().BeOfType<RecipeItemIngredient>()
            .Which.ItemCode.Should().Be(100);

        var keyword = recipe.Ingredients[1].Should().BeOfType<RecipeKeywordIngredient>().Subject;
        keyword.ItemKeys.Should().Equal(["Crystal"]);
        keyword.Desc.Should().Be("Auxiliary Crystal");
        keyword.StackSize.Should().Be(1);

        // Catalog index picks up the new keyword from items.json.
        svc.KeywordIndex.ItemsMatching(["Crystal"]).Select(i => i.InternalName)
            .Should().BeEquivalentTo(["RoughCrystal"]);
    }

    [Fact]
    public void Recipe_item_source_resolves_recipeId_to_recipe_InternalName()
    {
        // sources_items.json carries recipeId numerically; the parser should look up
        // the matching RecipeEntry and store its InternalName in ItemSource.Context.
        File.WriteAllText(Path.Combine(_bundledDir, "items.json"), """
            {
              "item_500": { "Name": "Tin Bar", "InternalName": "TinBar" }
            }
            """);
        File.WriteAllText(Path.Combine(_bundledDir, "items.meta.json"), "{\"cdnVersion\":\"v1\",\"source\":0}");
        File.WriteAllText(Path.Combine(_bundledDir, "recipes.json"), """
            {
              "recipe_101": {
                "Name": "Smelt Tin Bar",
                "InternalName": "SmeltTinBar",
                "Skill": "Smelting",
                "SkillLevelReq": 5
              }
            }
            """);
        File.WriteAllText(Path.Combine(_bundledDir, "recipes.meta.json"), "{\"cdnVersion\":\"v1\",\"source\":0}");
        File.WriteAllText(Path.Combine(_bundledDir, "sources_items.json"), """
            {
              "item_500": {
                "entries": [
                  { "recipeId": 101, "type": "Recipe" }
                ]
              }
            }
            """);
        File.WriteAllText(Path.Combine(_bundledDir, "sources_items.meta.json"), "{\"cdnVersion\":\"v1\",\"source\":0}");

        var svc = new ReferenceDataService(_cacheDir, NeverCallHttp(), bundledDir: _bundledDir);

        svc.ItemSources.Should().ContainKey("TinBar");
        var sources = svc.ItemSources["TinBar"];
        sources.Should().ContainSingle();
        sources[0].Type.Should().Be("Recipe");
        sources[0].Context.Should().Be("SmeltTinBar"); // InternalName, not the raw id
    }

    [Fact]
    public void GetSnapshot_UnknownKey_Throws()
    {
        WriteBundled("""{}""", version: "v100");
        var svc = new ReferenceDataService(_cacheDir, NeverCallHttp(), bundledDir: _bundledDir);
        var act = () => svc.GetSnapshot("nonexistent");
        act.Should().Throw<ArgumentException>();
    }

    private void WriteBundled(string body, string version)
    {
        File.WriteAllText(Path.Combine(_bundledDir, "items.json"), body);
        File.WriteAllText(Path.Combine(_bundledDir, "items.meta.json"),
            $"{{\"cdnVersion\":\"{version}\",\"source\":0}}");
    }

    private void WriteCache(string body, string version)
    {
        File.WriteAllText(Path.Combine(_cacheDir, "items.json"), body);
        File.WriteAllText(Path.Combine(_cacheDir, "items.meta.json"),
            $"{{\"cdnVersion\":\"{version}\",\"source\":1}}");
    }

    private static HttpResponseMessage Respond(string body, string contentType) =>
        new(HttpStatusCode.OK)
        {
            Content = new StringContent(body, System.Text.Encoding.UTF8, contentType),
        };

    private sealed class ThrowingHandler(string message) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
            => throw new InvalidOperationException(message);
    }

    private sealed class RoutingHandler(Func<HttpRequestMessage, HttpResponseMessage> route) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
            => Task.FromResult(route(request));
    }
}
