using System.IO;
using System.Net.Http;
using FluentAssertions;
using Mithril.Shared.Reference;
using Xunit;

namespace Mithril.Shared.Tests.Reference;

/// <summary>
/// Synthetic-fixture round-trip for the #249 StorageVault service plumbing: the
/// envelope-keyed <c>StorageVaults</c> lookup (including the <c>"*"</c>-prefixed
/// account-wide form), the polymorphic <c>Requirements</c> deserialization, and the
/// refresh path rebuilding the dictionary. Mirrors the #247 Lorebook plumbing test.
/// </summary>
[Trait("Category", "FileIO")]
[Collection("FileIO")]
public sealed class ReferenceDataServiceStorageVaultTests : IDisposable
{
    private readonly string _root;
    private readonly string _cacheDir;
    private readonly string _bundledDir;

    public ReferenceDataServiceStorageVaultTests()
    {
        _root = Mithril.TestSupport.TestPaths.CreateTempDir("mithril-ref-storagevault-tests");
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

    private const string StorageVaults = """
        {
          "*AccountStorage_Serbule": {
            "Area": "AreaSerbule",
            "HasAssociatedNpc": false,
            "ID": 105,
            "NpcFriendlyName": "Serbule Transfer Chest",
            "NumSlots": 0
          },
          "NPC_CharlesThompson": {
            "Area": "AreaSerbule",
            "Grouping": "AreaSerbule",
            "ID": 305,
            "Levels": { "Friends": 32, "CloseFriends": 40, "SoulMates": 64 },
            "NpcFriendlyName": "Charles Thompson",
            "RequiredItemKeywords": [ "Alchemy", "Potion" ],
            "RequirementDescription": "Potions and Alchemy Ingredients"
          },
          "IvynsChest": {
            "Area": "AreaSerbule",
            "ID": 303,
            "NpcFriendlyName": "Ivyn's Chest",
            "NumSlots": 32,
            "Requirements": { "InteractionFlag": "Ivyn_Gave_Passcode", "T": "InteractionFlagSet" }
          },
          "NPC_CynthiaRolfe": {
            "Area": "AreaStatehelm",
            "ID": 2003,
            "NpcFriendlyName": "Cynthia Rolfe",
            "Requirements": { "Quest": "GoblinsArmpitRoaches", "T": "QuestCompleted" }
          }
        }
        """;

    private void WriteFixture(string storageVaultsJson) =>
        File.WriteAllText(Path.Combine(_bundledDir, "storagevaults.json"), storageVaultsJson);

    [Fact]
    public void StorageVaults_KeyedByEnvelopeKey_IncludingStarPrefixed()
    {
        WriteFixture(StorageVaults);
        var svc = new ReferenceDataService(_cacheDir, NeverCallHttp(), bundledDir: _bundledDir);

        svc.StorageVaults.Should().ContainKey("*AccountStorage_Serbule");
        svc.StorageVaults.Should().ContainKey("NPC_CharlesThompson");
        svc.StorageVaults["*AccountStorage_Serbule"].NpcFriendlyName.Should().Be("Serbule Transfer Chest");
        svc.StorageVaults["*AccountStorage_Serbule"].HasAssociatedNpc.Should().BeFalse();
        svc.StorageVaults["*AccountStorage_Serbule"].NumSlots.Should().Be(0);
    }

    [Fact]
    public void StorageVaults_DeserialisesPolymorphicRequirements()
    {
        WriteFixture(StorageVaults);
        var svc = new ReferenceDataService(_cacheDir, NeverCallHttp(), bundledDir: _bundledDir);

        var ivyn = svc.StorageVaults["IvynsChest"];
        ivyn.Requirements.Should().ContainSingle()
            .Which.Should().BeOfType<Mithril.Reference.Models.Misc.StorageInteractionFlagSetRequirement>()
            .Which.InteractionFlag.Should().Be("Ivyn_Gave_Passcode");

        var cynthia = svc.StorageVaults["NPC_CynthiaRolfe"];
        cynthia.Requirements.Should().ContainSingle()
            .Which.Should().BeOfType<Mithril.Reference.Models.Misc.StorageQuestCompletedRequirement>()
            .Which.Quest.Should().Be("GoblinsArmpitRoaches");
    }

    [Fact]
    public void StorageVaults_FavorLevelsRoundTrip()
    {
        WriteFixture(StorageVaults);
        var svc = new ReferenceDataService(_cacheDir, NeverCallHttp(), bundledDir: _bundledDir);

        var charles = svc.StorageVaults["NPC_CharlesThompson"];
        charles.Levels.Should().NotBeNull();
        charles.Levels!["SoulMates"].Should().Be(64);
        charles.RequiredItemKeywords.Should().BeEquivalentTo(new[] { "Alchemy", "Potion" });
    }

    [Fact]
    public void StorageVaults_LoadsFromCache_OverBundled_ProvingParseAndSwapPath()
    {
        // LoadStorageVaults → LoadFile reads the cache file in preference to bundled. A
        // cached payload exercises the same ParseAndSwapStorageVaults path a live CDN
        // refresh runs, and proves the snapshot count is recomputed from the swapped set.
        WriteFixture(StorageVaults); // 4 bundled
        File.WriteAllText(Path.Combine(_cacheDir, "storagevaults.json"), """
            { "OnlyOne": { "ID": 9, "NpcFriendlyName": "Solo", "NumSlots": 5 } }
            """);
        File.WriteAllText(Path.Combine(_cacheDir, "storagevaults.meta.json"),
            "{\"cdnVersion\":\"v2\",\"source\":1}");

        var svc = new ReferenceDataService(_cacheDir, NeverCallHttp(), bundledDir: _bundledDir);

        svc.StorageVaults.Should().ContainSingle().Which.Key.Should().Be("OnlyOne");
        svc.GetSnapshot("storagevaults").EntryCount.Should().Be(1);
    }

    [Fact]
    public async Task RefreshStorageVaults_HttpUnavailable_KeepsExistingData_DoesNotThrow()
    {
        // RefreshAsync fetches from CDN; on failure it keeps the existing (bundled) set
        // rather than throwing — the documented refresh contract. This locks that the
        // "storagevaults" switch arm is wired (an unknown key would throw ArgumentException).
        WriteFixture(StorageVaults);
        var svc = new ReferenceDataService(_cacheDir, NeverCallHttp(), bundledDir: _bundledDir);

        var act = async () => await svc.RefreshAsync("storagevaults");
        await act.Should().NotThrowAsync();
        svc.StorageVaults.Should().HaveCount(4);
    }

    [Fact]
    public void StorageVaults_IsAKnownRefreshKey()
    {
        WriteFixture(StorageVaults);
        var svc = new ReferenceDataService(_cacheDir, NeverCallHttp(), bundledDir: _bundledDir);

        svc.Keys.Should().Contain("storagevaults");
        // GetSnapshot must not throw for the new key.
        svc.GetSnapshot("storagevaults").Key.Should().Be("storagevaults");
    }

    [Fact]
    public void RealBundledStorageVaultsJson_ParsesAndCoversKnownVariants()
    {
        var realBundled = Path.Combine(AppContext.BaseDirectory, "Reference", "BundledData");
        if (!File.Exists(Path.Combine(realBundled, "storagevaults.json"))) return;

        var svc = new ReferenceDataService(_cacheDir, NeverCallHttp(), bundledDir: realBundled);

        svc.StorageVaults.Should().NotBeEmpty();
        // The three real-data variants the handoff calls out.
        svc.StorageVaults.Should().ContainKey("*AccountStorage_Serbule")
            .WhoseValue.HasAssociatedNpc.Should().BeFalse("transfer chest");
        svc.StorageVaults["NPC_CharlesThompson"].Levels.Should()
            .NotBeNull("favor-scaled vendor vault");
        svc.StorageVaults["IvynsChest"].Requirements.Should()
            .ContainSingle().Which.Should()
            .BeOfType<Mithril.Reference.Models.Misc.StorageInteractionFlagSetRequirement>();
        // No UnknownStorageRequirement should appear in the current corpus (drift guard —
        // if PG adds a new discriminator this surfaces here before the UI degrades).
        svc.StorageVaults.Values
            .SelectMany(v => v.Requirements ?? Array.Empty<Mithril.Reference.Models.Misc.StorageRequirement>())
            .OfType<Mithril.Reference.Models.Misc.UnknownStorageRequirement>()
            .Should().BeEmpty("the modelled subclasses cover the entire bundled corpus");
    }

    private sealed class ThrowingHandler(string message) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken) =>
            throw new InvalidOperationException(message);
    }
}
