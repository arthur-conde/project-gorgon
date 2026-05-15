using System.IO;
using System.Net.Http;
using FluentAssertions;
using Mithril.Shared.Reference;
using Xunit;

namespace Mithril.Shared.Tests.Reference;

/// <summary>
/// Synthetic-fixture round-trip for the #247 lorebook service plumbing: the three lorebook
/// lookups (envelope / InternalName / numeric id), the LorebookInfo sidecar, and the #318
/// popup-from-index source <c>ItemsBestowingLorebook</c> with its cross-trigger matrix.
/// </summary>
[Trait("Category", "FileIO")]
[Collection("FileIO")]
public sealed class ReferenceDataServiceLorebookCrossLinkIndexTests : IDisposable
{
    private readonly string _root;
    private readonly string _cacheDir;
    private readonly string _bundledDir;

    public ReferenceDataServiceLorebookCrossLinkIndexTests()
    {
        _root = Mithril.TestSupport.TestPaths.CreateTempDir("mithril-ref-lorebook-tests");
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

    private void WriteFixture(string itemsJson, string lorebooksJson, string lorebookInfoJson)
    {
        File.WriteAllText(Path.Combine(_bundledDir, "items.json"), itemsJson);
        File.WriteAllText(Path.Combine(_bundledDir, "lorebooks.json"), lorebooksJson);
        File.WriteAllText(Path.Combine(_bundledDir, "lorebookinfo.json"), lorebookInfoJson);
    }

    private const string Items = """
        {
          "item_14036": { "Name": "The Gods of Knowledge Vol 1", "InternalName": "TheGodsOfKnowledgeVol1", "BestowLoreBook": 101 },
          "item_99999": { "Name": "Unrelated Trinket", "InternalName": "Trinket" }
        }
        """;

    private const string Lorebooks = """
        {
          "Book_101": {
            "Category": "Stories",
            "InternalName": "TheWastedWishes",
            "IsClientLocal": true,
            "Keywords": [ "AreaSerbule" ],
            "Title": "The Wasted Wishes",
            "Visibility": "GhostedUntilFound",
            "Text": "<h1>The Wasted Wishes</h1>Once upon a time...",
            "LocationHint": "Found in a house in Serbule"
          },
          "Book_103": {
            "Category": "Gods",
            "InternalName": "TheChaliceSagaVol1",
            "Title": "The Chalice Saga Vol 1",
            "Text": "<h1>The Chalice Saga</h1>..."
          }
        }
        """;

    private const string LorebookInfo = """
        {
          "Categories": {
            "Gods":    { "Title": "The Gods", "SubTitle": "Gods, Myths, and Legends", "SortTitle": "Gods" },
            "Stories": { "Title": "Stories", "SubTitle": "With Debatable Real-World Accuracy" },
            "Misc":    { "Title": "Miscellaneous", "SortTitle": "zzzMiscellaneous" }
          }
        }
        """;

    [Fact]
    public void LorebooksByInternalName_KeyedOnInternalNameNotEnvelope()
    {
        WriteFixture(Items, Lorebooks, LorebookInfo);
        var svc = new ReferenceDataService(_cacheDir, NeverCallHttp(), bundledDir: _bundledDir);

        svc.LorebooksByInternalName.Should().ContainKey("TheWastedWishes");
        svc.LorebooksByInternalName.Should().NotContainKey("Book_101");
        svc.Lorebooks.Should().ContainKey("Book_101");
        svc.Lorebooks.Should().NotContainKey("TheWastedWishes");
        // Same POCO behind both keys, two different identifiers.
        svc.LorebooksByInternalName["TheWastedWishes"].Should()
            .BeSameAs(svc.Lorebooks["Book_101"]);
    }

    [Fact]
    public void LorebooksById_LiftsNumericIdFromEnvelopeKey()
    {
        WriteFixture(Items, Lorebooks, LorebookInfo);
        var svc = new ReferenceDataService(_cacheDir, NeverCallHttp(), bundledDir: _bundledDir);

        svc.LorebooksById.Should().ContainKey(101);
        svc.LorebooksById[101].InternalName.Should().Be("TheWastedWishes");
        svc.LorebooksById.Should().ContainKey(103);
    }

    [Fact]
    public void ItemsBestowingLorebook_IndexesByItemBestowLoreBook()
    {
        WriteFixture(Items, Lorebooks, LorebookInfo);
        var svc = new ReferenceDataService(_cacheDir, NeverCallHttp(), bundledDir: _bundledDir);

        // item_14036 has BestowLoreBook=101 → resolves to Book_101 → "TheWastedWishes".
        svc.ItemsBestowingLorebook.Should().ContainKey("TheWastedWishes");
        svc.ItemsBestowingLorebook["TheWastedWishes"].Should().ContainSingle()
            .Which.InternalName.Should().Be("TheGodsOfKnowledgeVol1");
        // The book nobody bestows is absent (no empty-list noise).
        svc.ItemsBestowingLorebook.Should().NotContainKey("TheChaliceSagaVol1");
    }

    [Fact]
    public void ItemsBestowingLorebook_IsDedupedByItemInternalName()
    {
        // Two item entries with the same InternalName + same BestowLoreBook must collapse
        // to one member so the popup count equals distinct membership (#318 invariant).
        WriteFixture(
            itemsJson: """
            {
              "item_1": { "Name": "Book Item", "InternalName": "BookItem", "BestowLoreBook": 101 },
              "item_1_dup": { "Name": "Book Item", "InternalName": "BookItem", "BestowLoreBook": 101 }
            }
            """,
            Lorebooks, LorebookInfo);
        var svc = new ReferenceDataService(_cacheDir, NeverCallHttp(), bundledDir: _bundledDir);

        svc.ItemsBestowingLorebook["TheWastedWishes"].Should().ContainSingle();
    }

    [Fact]
    public void RefreshItemsOrLorebooks_RebuildsItemsBestowingLorebook()
    {
        // Trigger-matrix proof via the established cache-reconstruction pattern (mirrors
        // ReferenceDataServiceAreaCrossLinkIndexTests): a fresh service over a rewritten
        // cache file exercises the same ParseAndSwap* → Build…CrossLinkIndex path a live
        // RefreshAsync would, without an HTTP round-trip.
        WriteFixture(
            itemsJson: """{ "item_x": { "Name": "Nothing", "InternalName": "Nothing" } }""",
            Lorebooks, LorebookInfo);
        var svc = new ReferenceDataService(_cacheDir, NeverCallHttp(), bundledDir: _bundledDir);
        svc.ItemsBestowingLorebook.Should().BeEmpty(
            "no item bestows a lorebook in the initial fixture");

        // Trigger 1 (items.json side): a cached items.json carrying a bestowing item.
        // ParseAndSwapItems must call BuildLorebookItemCrossLinkIndex.
        File.WriteAllText(Path.Combine(_cacheDir, "items.json"), Items);
        File.WriteAllText(Path.Combine(_cacheDir, "items.meta.json"), "{\"cdnVersion\":\"v2\",\"source\":1}");
        var afterItems = new ReferenceDataService(_cacheDir, NeverCallHttp(), bundledDir: _bundledDir);
        afterItems.ItemsBestowingLorebook.Should().ContainKey("TheWastedWishes");

        // Trigger 2 (lorebooks.json side): a cached lorebooks.json that re-maps id 101 to a
        // *different* InternalName. ParseAndSwapLorebooks must rebuild the index against the
        // current item set so the membership re-keys to the new book name.
        File.WriteAllText(Path.Combine(_cacheDir, "lorebooks.json"), """
        {
          "Book_101": { "Category": "Stories", "InternalName": "RenamedBook", "Text": "x" }
        }
        """);
        File.WriteAllText(Path.Combine(_cacheDir, "lorebooks.meta.json"), "{\"cdnVersion\":\"v2\",\"source\":1}");
        var afterLorebooks = new ReferenceDataService(_cacheDir, NeverCallHttp(), bundledDir: _bundledDir);
        afterLorebooks.ItemsBestowingLorebook.Should().ContainKey("RenamedBook",
            "a lorebooks-only refresh must rebuild the index so the item re-keys to the new InternalName");
        afterLorebooks.ItemsBestowingLorebook.Should().NotContainKey("TheWastedWishes");
        afterLorebooks.ItemsBestowingLorebook["RenamedBook"].Should().ContainSingle();
    }

    [Fact]
    public void LorebookCategories_ParseFromSidecar()
    {
        WriteFixture(Items, Lorebooks, LorebookInfo);
        var svc = new ReferenceDataService(_cacheDir, NeverCallHttp(), bundledDir: _bundledDir);

        svc.LorebookCategories.Should().ContainKey("Gods");
        svc.LorebookCategories["Gods"].Title.Should().Be("The Gods");
        svc.LorebookCategories["Gods"].SubTitle.Should().Be("Gods, Myths, and Legends");
        svc.LorebookCategories["Misc"].SortTitle.Should().Be("zzzMiscellaneous");
        svc.LorebookCategories.Should().HaveCount(3);
    }

    private sealed class ThrowingHandler(string message) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken) =>
            throw new InvalidOperationException(message);
    }
}
