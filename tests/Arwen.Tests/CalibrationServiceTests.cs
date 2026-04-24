using System.IO;
using System.Text.Json;
using Arwen.Domain;
using FluentAssertions;
using Gorgon.Shared.Reference;
using Xunit;

namespace Arwen.Tests;

public sealed class CalibrationServiceTests
{
    private static IReferenceDataService BuildRefData()
    {
        var items = new Dictionary<long, ItemEntry>
        {
            [1] = new(1, "Moonstone", "Moonstone", 1, 0,
                [new ItemKeyword("Crystal", 0), new ItemKeyword("Moonstone", 500)],
                Value: 100m),
            [2] = new(2, "Apple", "Apple", 1, 0,
                [new ItemKeyword("Fruit", 0)],
                Value: 5m),
            // Ring items sharing a "Ring" + "MinRarity:Rare" signature with Makara
            [3] = new(3, "StafflordsRing", "StafflordsRing", 1, 0,
                [new ItemKeyword("Equipment", 0), new ItemKeyword("Jewelry", 0), new ItemKeyword("Ring", 0), new ItemKeyword("MinRarity:Rare", 0)],
                Value: 150m),
            [4] = new(4, "Mindroot", "Mindroot", 1, 0,
                [new ItemKeyword("Equipment", 0), new ItemKeyword("Jewelry", 0), new ItemKeyword("Ring", 0), new ItemKeyword("MinRarity:Rare", 0)],
                Value: 105m),
            // A rare item with no extra distinctive keywords (baseline rare signature)
            [5] = new(5, "PlainRareThing", "PlainRareThing", 1, 0,
                [new ItemKeyword("Equipment", 0), new ItemKeyword("MinRarity:Rare", 0)],
                Value: 90m),
            // For dislike-test: a common necklace that Yetta likes (Amulet) but also dislikes (TestDislike)
            [6] = new(6, "TestDislikedNecklace", "TestDislikedNecklace", 1, 0,
                [new ItemKeyword("Amulet", 0), new ItemKeyword("TestDislike", 0)],
                Value: 100m),
        };
        var npcs = new Dictionary<string, NpcEntry>(StringComparer.Ordinal)
        {
            ["NPC_Sanja"] = new("NPC_Sanja", "Sanja", "Serbule",
                [new NpcPreference("Love", ["Moonstone"], "Moonstones", 1.5, null)],
                ["Friends"], []),
            ["NPC_Test"] = new("NPC_Test", "Test", "Serbule",
                [new NpcPreference("Love", ["Fruit"], "Fruit", 2.0, null)],
                ["Friends"], []),
            ["NPC_Makara"] = new("NPC_Makara", "Makara", "Serbule",
                [
                    new NpcPreference("Love", ["MinRarity:Rare"], "Rare or Better Magic Gear", 2.0, null),
                    new NpcPreference("Love", ["Ring"], "Rings", 1.0, null),
                ],
                ["Friends"], []),
            ["NPC_Yetta"] = new("NPC_Yetta", "Yetta", "Serbule",
                [
                    new NpcPreference("Love", ["Amulet"], "Necklaces", 2.0, null),
                    new NpcPreference("Dislike", ["TestDislike"], "Yetta Test Dislikes", -5.0, null),
                ],
                ["Friends"], []),
        };
        return new FakeRefData(items, npcs);
    }

    private static (CalibrationService svc, GiftIndex index) BuildService(string dataDir)
    {
        var refData = BuildRefData();
        var index = new GiftIndex();
        index.Build(refData.Items, refData.Npcs);
        var svc = new CalibrationService(refData, index, dataDir);
        return (svc, index);
    }

    [Fact]
    public void DetectsGiftAndRecordsFullKeywords()
    {
        var dir = Path.Combine(Path.GetTempPath(), $"arwen_test_{Guid.NewGuid():N}");
        try
        {
            var (svc, _) = BuildService(dir);

            svc.OnItemAdded("Moonstone", 12345);
            svc.OnStartInteraction("NPC_Sanja");
            svc.OnItemDeleted(12345);
            svc.OnDeltaFavor("NPC_Sanja", 22.5);

            svc.Data.Observations.Should().HaveCount(1);
            var obs = svc.Data.Observations[0];
            obs.NpcKey.Should().Be("NPC_Sanja");
            obs.ItemInternalName.Should().Be("Moonstone");
            obs.FavorDelta.Should().Be(22.5);
            obs.ItemKeywords.Should().BeEquivalentTo("Crystal", "Moonstone");
            obs.MatchedPreferences.Should().HaveCount(1);
            obs.MatchedPreferences[0].Name.Should().Be("Moonstones");
            obs.MatchedPreferences[0].Pref.Should().Be(1.5);
            obs.EffectivePref.Should().Be(1.5);

            // rate = 22.5 / (1.5 * 100) = 0.15
            obs.DerivedRate.Should().BeApproximately(0.15, 0.001);
            svc.GetRate("Moonstone").Should().BeApproximately(0.15, 0.001);
        }
        finally
        {
            if (Directory.Exists(dir)) Directory.Delete(dir, true);
        }
    }

    [Fact]
    public void RecordsAllMatchingPreferences_SignatureCoversMultiplePrefs()
    {
        var dir = Path.Combine(Path.GetTempPath(), $"arwen_test_{Guid.NewGuid():N}");
        try
        {
            var (svc, _) = BuildService(dir);

            // StafflordsRing matches both Makara prefs: "MinRarity:Rare" (pref=2) and "Ring" (pref=1)
            svc.OnItemAdded("StafflordsRing", 1001);
            svc.OnStartInteraction("NPC_Makara");
            svc.OnItemDeleted(1001);
            svc.OnDeltaFavor("NPC_Makara", 48.7557);

            var obs = svc.Data.Observations[0];
            obs.MatchedPreferences.Should().HaveCount(2);
            obs.EffectivePref.Should().Be(3.0); // 2 + 1
            obs.Signature.Should().Be("Rare or Better Magic Gear,Rings"); // sorted
            // rate = 48.7557 / (150 * 3) = 0.108346
            obs.DerivedRate.Should().BeApproximately(0.10834, 0.0001);
        }
        finally
        {
            if (Directory.Exists(dir)) Directory.Delete(dir, true);
        }
    }

    [Fact]
    public void EstimateFavor_PrefersItemRate()
    {
        var dir = Path.Combine(Path.GetTempPath(), $"arwen_test_{Guid.NewGuid():N}");
        try
        {
            var (svc, index) = BuildService(dir);

            svc.OnItemAdded("Moonstone", 100);
            svc.OnStartInteraction("NPC_Sanja");
            svc.OnItemDeleted(100);
            svc.OnDeltaFavor("NPC_Sanja", 22.5);

            var match = index.MatchItemToNpc(1, "NPC_Sanja");
            match.Should().NotBeNull();
            var est = svc.EstimateFavor(match!, "NPC_Sanja");
            est.Should().NotBeNull();
            est!.Tier.Should().Be("Item");
            est.Value.Should().BeApproximately(22.5, 0.01);
        }
        finally
        {
            if (Directory.Exists(dir)) Directory.Delete(dir, true);
        }
    }

    [Fact]
    public void EstimateFavor_FallsBackThroughHierarchy()
    {
        var dir = Path.Combine(Path.GetTempPath(), $"arwen_test_{Guid.NewGuid():N}");
        try
        {
            var (svc, index) = BuildService(dir);

            // Record a gift of StafflordsRing — populates ItemRate for (Makara, StafflordsRing)
            // plus SignatureRate for (Makara, "Rare or Better Magic Gear,Rings") and NPC baseline.
            svc.OnItemAdded("StafflordsRing", 1001);
            svc.OnStartInteraction("NPC_Makara");
            svc.OnItemDeleted(1001);
            svc.OnDeltaFavor("NPC_Makara", 48.7557);

            // Estimating the SAME item → Item tier
            var stafflords = index.MatchItemToNpc(3, "NPC_Makara")!;
            svc.EstimateFavor(stafflords, "NPC_Makara")!.Tier.Should().Be("Item");

            // Estimating Mindroot (same ring signature) → Signature tier
            var mindroot = index.MatchItemToNpc(4, "NPC_Makara")!;
            svc.EstimateFavor(mindroot, "NPC_Makara")!.Tier.Should().Be("Signature");

            // Estimating PlainRareThing (different signature — only MinRarity:Rare, no Ring) → NPC baseline
            var plain = index.MatchItemToNpc(5, "NPC_Makara")!;
            svc.EstimateFavor(plain, "NPC_Makara")!.Tier.Should().Be("NPC");
        }
        finally
        {
            if (Directory.Exists(dir)) Directory.Delete(dir, true);
        }
    }

    [Fact]
    public void EstimateFavor_ReturnsNullWhenNoCalibration()
    {
        var dir = Path.Combine(Path.GetTempPath(), $"arwen_test_{Guid.NewGuid():N}");
        try
        {
            var (svc, index) = BuildService(dir);
            var match = index.MatchItemToNpc(1, "NPC_Sanja");
            svc.EstimateFavor(match!, "NPC_Sanja").Should().BeNull();
        }
        finally
        {
            if (Directory.Exists(dir)) Directory.Delete(dir, true);
        }
    }

    [Fact]
    public void DetectsGift_DeltaBeforeDelete()
    {
        var dir = Path.Combine(Path.GetTempPath(), $"arwen_test_{Guid.NewGuid():N}");
        try
        {
            var (svc, _) = BuildService(dir);

            svc.OnItemAdded("Moonstone", 12345);
            svc.OnStartInteraction("NPC_Sanja");
            svc.OnDeltaFavor("NPC_Sanja", 22.5);
            svc.OnItemDeleted(12345);

            svc.Data.Observations.Should().HaveCount(1);
            var obs = svc.Data.Observations[0];
            obs.DerivedRate.Should().BeApproximately(0.15, 0.001);
        }
        finally
        {
            if (Directory.Exists(dir)) Directory.Delete(dir, true);
        }
    }

    [Fact]
    public void IgnoresDeleteWithoutActiveNpc()
    {
        var dir = Path.Combine(Path.GetTempPath(), $"arwen_test_{Guid.NewGuid():N}");
        try
        {
            var (svc, _) = BuildService(dir);

            svc.OnItemAdded("Moonstone", 100);
            svc.OnItemDeleted(100);
            svc.OnDeltaFavor("NPC_Sanja", 22.5);

            svc.Data.Observations.Should().BeEmpty();
        }
        finally
        {
            if (Directory.Exists(dir)) Directory.Delete(dir, true);
        }
    }

    [Fact]
    public void IgnoresNegativeDelta()
    {
        var dir = Path.Combine(Path.GetTempPath(), $"arwen_test_{Guid.NewGuid():N}");
        try
        {
            var (svc, _) = BuildService(dir);

            svc.OnItemAdded("Moonstone", 100);
            svc.OnStartInteraction("NPC_Sanja");
            svc.OnItemDeleted(100);
            svc.OnDeltaFavor("NPC_Sanja", -10);

            svc.Data.Observations.Should().BeEmpty();
        }
        finally
        {
            if (Directory.Exists(dir)) Directory.Delete(dir, true);
        }
    }

    [Fact]
    public void SkipsObservation_WhenEffectivePrefNonPositive()
    {
        var dir = Path.Combine(Path.GetTempPath(), $"arwen_test_{Guid.NewGuid():N}");
        try
        {
            var (svc, _) = BuildService(dir);

            // TestDislikedNecklace matches Yetta's Amulet (pref=2) and TestDislike (pref=-5) → net -3
            svc.OnItemAdded("TestDislikedNecklace", 7777);
            svc.OnStartInteraction("NPC_Yetta");
            svc.OnItemDeleted(7777);
            svc.OnDeltaFavor("NPC_Yetta", 10); // would be positive but pref math is negative

            svc.Data.Observations.Should().BeEmpty();
        }
        finally
        {
            if (Directory.Exists(dir)) Directory.Delete(dir, true);
        }
    }

    [Fact]
    public void ExportImport_Roundtrips()
    {
        var dir1 = Path.Combine(Path.GetTempPath(), $"arwen_test_{Guid.NewGuid():N}");
        var dir2 = Path.Combine(Path.GetTempPath(), $"arwen_test_{Guid.NewGuid():N}");
        try
        {
            var (svc1, _) = BuildService(dir1);
            svc1.OnItemAdded("Moonstone", 100);
            svc1.OnStartInteraction("NPC_Sanja");
            svc1.OnItemDeleted(100);
            svc1.OnDeltaFavor("NPC_Sanja", 22.5);

            var json = svc1.ExportJson("test contributor");

            var (svc2, _) = BuildService(dir2);
            var imported = svc2.ImportJson(json);
            imported.Should().Be(1);
            svc2.Data.Observations.Should().HaveCount(1);
            svc2.GetRate("Moonstone").Should().BeApproximately(0.15, 0.001);
        }
        finally
        {
            if (Directory.Exists(dir1)) Directory.Delete(dir1, true);
            if (Directory.Exists(dir2)) Directory.Delete(dir2, true);
        }
    }

    [Fact]
    public void Import_DeduplicatesObservations()
    {
        var dir = Path.Combine(Path.GetTempPath(), $"arwen_test_{Guid.NewGuid():N}");
        try
        {
            var (svc, _) = BuildService(dir);
            svc.OnItemAdded("Moonstone", 100);
            svc.OnStartInteraction("NPC_Sanja");
            svc.OnItemDeleted(100);
            svc.OnDeltaFavor("NPC_Sanja", 22.5);

            var json = svc.ExportJson();
            var imported = svc.ImportJson(json);
            imported.Should().Be(0);
            svc.Data.Observations.Should().HaveCount(1);
        }
        finally
        {
            if (Directory.Exists(dir)) Directory.Delete(dir, true);
        }
    }

    [Fact]
    public void MultipleObservations_AverageRate()
    {
        var dir = Path.Combine(Path.GetTempPath(), $"arwen_test_{Guid.NewGuid():N}");
        try
        {
            var (svc, _) = BuildService(dir);

            svc.OnItemAdded("Moonstone", 100);
            svc.OnStartInteraction("NPC_Sanja");
            svc.OnItemDeleted(100);
            svc.OnDeltaFavor("NPC_Sanja", 22.5); // rate = 0.15

            svc.OnItemAdded("Moonstone", 101);
            svc.OnStartInteraction("NPC_Sanja");
            svc.OnItemDeleted(101);
            svc.OnDeltaFavor("NPC_Sanja", 24.0); // rate = 0.16

            svc.GetRate("Moonstone").Should().BeApproximately(0.155, 0.001);

            var itemRate = svc.Data.ItemRates[$"NPC_Sanja|Moonstone"];
            itemRate.SampleCount.Should().Be(2);
            itemRate.MinRate.Should().BeApproximately(0.15, 0.001);
            itemRate.MaxRate.Should().BeApproximately(0.16, 0.001);
        }
        finally
        {
            if (Directory.Exists(dir)) Directory.Delete(dir, true);
        }
    }

    [Fact]
    public void ExportCommunityJson_ContainsNoRawObservations()
    {
        var dir = Path.Combine(Path.GetTempPath(), $"arwen_test_{Guid.NewGuid():N}");
        try
        {
            var (svc, _) = BuildService(dir);
            svc.OnItemAdded("Moonstone", 100);
            svc.OnStartInteraction("NPC_Sanja");
            svc.OnItemDeleted(100);
            svc.OnDeltaFavor("NPC_Sanja", 22.5);

            svc.Data.Observations.Should().NotBeEmpty();

            var json = svc.ExportCommunityJson("a note");

            json.Should().NotContain("observations", because: "community payload carries rates only");
            json.Should().NotContain("itemKeywords", because: "per-item keyword lists are observation-scoped");
            json.Should().NotContain("matchedPreferences", because: "matched preference lists are observation-scoped");
            json.Should().NotContain("derivedRate", because: "observation-scoped derived fields don't belong in aggregates");

            json.Should().Contain("\"schemaVersion\": 2");
            json.Should().Contain("\"module\": \"arwen\"");
            json.Should().Contain("\"itemRates\"");
            json.Should().Contain("\"signatureRates\"");
            json.Should().Contain("\"npcRates\"");
            json.Should().Contain("\"keywordRates\"");
            json.Should().Contain("Moonstone");
        }
        finally
        {
            if (Directory.Exists(dir)) Directory.Delete(dir, true);
        }
    }

    [Fact]
    public void MigratesV1CalibrationFile_PopulatesKeywordsAndPreferences()
    {
        var dir = Path.Combine(Path.GetTempPath(), $"arwen_test_{Guid.NewGuid():N}");
        try
        {
            Directory.CreateDirectory(dir);
            var v1Path = Path.Combine(dir, "calibration.json");
            // A v1-shape file: old schema with matchedKeyword/pref/derivedRate and no itemKeywords/matchedPreferences.
            var v1Json = """
                {
                  "version": 1,
                  "observations": [
                    {
                      "npcKey": "NPC_Sanja",
                      "itemInternalName": "Moonstone",
                      "matchedKeyword": "Moonstone",
                      "itemValue": 100,
                      "pref": 1.5,
                      "favorDelta": 22.5,
                      "derivedRate": 0.15,
                      "timestamp": "2026-04-20T00:00:00+00:00"
                    },
                    {
                      "npcKey": "NPC_Sanja",
                      "itemInternalName": "DoesNotExistAnymore",
                      "matchedKeyword": "Obsolete",
                      "itemValue": 50,
                      "pref": 1.0,
                      "favorDelta": 10,
                      "derivedRate": 0.2,
                      "timestamp": "2026-04-20T00:00:00+00:00"
                    }
                  ]
                }
                """;
            File.WriteAllText(v1Path, v1Json);

            var refData = BuildRefData();
            var index = new GiftIndex();
            index.Build(refData.Items, refData.Npcs);
            var svc = new CalibrationService(refData, index, dir);

            svc.Data.Version.Should().Be(CalibrationService.CurrentSchemaVersion);
            svc.Data.Observations.Should().HaveCount(1); // unknown item dropped
            var obs = svc.Data.Observations[0];
            obs.ItemInternalName.Should().Be("Moonstone");
            obs.ItemKeywords.Should().NotBeEmpty();
            obs.MatchedPreferences.Should().HaveCount(1);
            obs.MatchedPreferences[0].Name.Should().Be("Moonstones");

            // File should now be v2 on disk
            var savedBytes = File.ReadAllBytes(v1Path);
            using var doc = JsonDocument.Parse(savedBytes);
            doc.RootElement.GetProperty("version").GetInt32().Should().Be(CalibrationService.CurrentSchemaVersion);
        }
        finally
        {
            if (Directory.Exists(dir)) Directory.Delete(dir, true);
        }
    }

    // ── Fake IReferenceDataService ──────────────────────────────────

    private sealed class FakeRefData : IReferenceDataService
    {
        private readonly Dictionary<long, ItemEntry> _items;
        private readonly Dictionary<string, ItemEntry> _byName;
        private readonly Dictionary<string, NpcEntry> _npcs;

        public FakeRefData(Dictionary<long, ItemEntry> items, Dictionary<string, NpcEntry> npcs)
        {
            _items = items;
            _npcs = npcs;
            _byName = items.Values.ToDictionary(i => i.InternalName, StringComparer.Ordinal);
        }

        public IReadOnlyList<string> Keys { get; } = ["items", "npcs"];
        public IReadOnlyDictionary<long, ItemEntry> Items => _items;
        public IReadOnlyDictionary<string, ItemEntry> ItemsByInternalName => _byName;
        public IReadOnlyDictionary<string, RecipeEntry> Recipes { get; } = new Dictionary<string, RecipeEntry>();
        public IReadOnlyDictionary<string, RecipeEntry> RecipesByInternalName { get; } = new Dictionary<string, RecipeEntry>();
        public IReadOnlyDictionary<string, SkillEntry> Skills { get; } = new Dictionary<string, SkillEntry>();
        public IReadOnlyDictionary<string, XpTableEntry> XpTables { get; } = new Dictionary<string, XpTableEntry>();
        public IReadOnlyDictionary<string, NpcEntry> Npcs => _npcs;
        public IReadOnlyDictionary<string, IReadOnlyList<ItemSource>> ItemSources { get; } = new Dictionary<string, IReadOnlyList<ItemSource>>();
        public IReadOnlyDictionary<string, AttributeEntry> Attributes { get; } = new Dictionary<string, AttributeEntry>();
        public ReferenceFileSnapshot GetSnapshot(string key) => new(key, ReferenceFileSource.Bundled, "test", null, 0);
        public Task RefreshAsync(string key, CancellationToken ct = default) => Task.CompletedTask;
        public Task RefreshAllAsync(CancellationToken ct = default) => Task.CompletedTask;
        public void BeginBackgroundRefresh() { }
        public event EventHandler<string>? FileUpdated { add { } remove { } }
    }
}
