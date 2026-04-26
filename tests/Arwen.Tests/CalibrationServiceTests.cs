using System.IO;
using System.Text.Json;
using Arwen.Domain;
using FluentAssertions;
using Mithril.Shared.Inventory;
using Mithril.Shared.Reference;
using Xunit;

namespace Arwen.Tests;

[Trait("Category", "FileIO")]
[Collection("FileIO")]
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
            // Stackable test item — exercises the v3 stackable-skip guard and migration partition.
            // Sanja loves Phlogiston (re-uses the same Crystal/Moonstone love tier for test simplicity).
            [7] = new(7, "Phlogiston1", "Phlogiston1", MaxStackSize: 10, IconId: 0,
                [new ItemKeyword("Crystal", 0), new ItemKeyword("Moonstone", 500)],
                Value: 5m),
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

    private static (CalibrationService svc, GiftIndex index, FakeInventory inv) BuildService(string dataDir)
    {
        var refData = BuildRefData();
        var index = new GiftIndex();
        index.Build(refData.Items, refData.Npcs);
        var inv = new FakeInventory();
        var svc = new CalibrationService(refData, index, inv, dataDir);
        return (svc, index, inv);
    }

    /// <summary>Best-effort recursive delete. Defender / Search indexer occasionally
    /// hold a transient handle on freshly closed files in %TEMP% under parallel-test
    /// load, so cleanup may throw IOException or DirectoryNotFoundException — neither
    /// of which should fail an otherwise-passing test.</summary>
    private static void SafeDeleteDir(string dir)
    {
        if (!Directory.Exists(dir)) return;
        try { Directory.Delete(dir, recursive: true); } catch { /* best-effort */ }
    }

    [Fact]
    public void DetectsGiftAndRecordsFullKeywords()
    {
        var dir = Mithril.TestSupport.TestPaths.CreateTempDir("arwen_test");
        try
        {
            var (svc, _, inv) = BuildService(dir);

            inv.Add(12345, "Moonstone");
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
            SafeDeleteDir(dir);
        }
    }

    [Fact]
    public void RecordsAllMatchingPreferences_SignatureCoversMultiplePrefs()
    {
        var dir = Mithril.TestSupport.TestPaths.CreateTempDir("arwen_test");
        try
        {
            var (svc, _, inv) = BuildService(dir);

            // StafflordsRing matches both Makara prefs: "MinRarity:Rare" (pref=2) and "Ring" (pref=1)
            inv.Add(1001, "StafflordsRing");
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
            SafeDeleteDir(dir);
        }
    }

    [Fact]
    public void EstimateFavor_PrefersItemRate()
    {
        var dir = Mithril.TestSupport.TestPaths.CreateTempDir("arwen_test");
        try
        {
            var (svc, index, inv) = BuildService(dir);

            inv.Add(100, "Moonstone");
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
            SafeDeleteDir(dir);
        }
    }

    [Fact]
    public void EstimateFavor_FallsBackThroughHierarchy()
    {
        var dir = Mithril.TestSupport.TestPaths.CreateTempDir("arwen_test");
        try
        {
            var (svc, index, inv) = BuildService(dir);

            // Record a gift of StafflordsRing — populates ItemRate for (Makara, StafflordsRing)
            // plus SignatureRate for (Makara, "Rare or Better Magic Gear,Rings") and NPC baseline.
            inv.Add(1001, "StafflordsRing");
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
            SafeDeleteDir(dir);
        }
    }

    [Fact]
    public void EstimateFavor_ReturnsNullWhenNoCalibration()
    {
        var dir = Mithril.TestSupport.TestPaths.CreateTempDir("arwen_test");
        try
        {
            var (svc, index, inv) = BuildService(dir);
            var match = index.MatchItemToNpc(1, "NPC_Sanja");
            svc.EstimateFavor(match!, "NPC_Sanja").Should().BeNull();
        }
        finally
        {
            SafeDeleteDir(dir);
        }
    }

    [Fact]
    public void DetectsGift_DeltaBeforeDelete()
    {
        var dir = Mithril.TestSupport.TestPaths.CreateTempDir("arwen_test");
        try
        {
            var (svc, _, inv) = BuildService(dir);

            inv.Add(12345, "Moonstone");
            svc.OnStartInteraction("NPC_Sanja");
            svc.OnDeltaFavor("NPC_Sanja", 22.5);
            svc.OnItemDeleted(12345);

            svc.Data.Observations.Should().HaveCount(1);
            var obs = svc.Data.Observations[0];
            obs.DerivedRate.Should().BeApproximately(0.15, 0.001);
        }
        finally
        {
            SafeDeleteDir(dir);
        }
    }

    [Fact]
    public void IgnoresDeleteWithoutActiveNpc()
    {
        var dir = Mithril.TestSupport.TestPaths.CreateTempDir("arwen_test");
        try
        {
            var (svc, _, inv) = BuildService(dir);

            inv.Add(100, "Moonstone");
            svc.OnItemDeleted(100);
            svc.OnDeltaFavor("NPC_Sanja", 22.5);

            svc.Data.Observations.Should().BeEmpty();
        }
        finally
        {
            SafeDeleteDir(dir);
        }
    }

    [Fact]
    public void IgnoresNegativeDelta()
    {
        var dir = Mithril.TestSupport.TestPaths.CreateTempDir("arwen_test");
        try
        {
            var (svc, _, inv) = BuildService(dir);

            inv.Add(100, "Moonstone");
            svc.OnStartInteraction("NPC_Sanja");
            svc.OnItemDeleted(100);
            svc.OnDeltaFavor("NPC_Sanja", -10);

            svc.Data.Observations.Should().BeEmpty();
        }
        finally
        {
            SafeDeleteDir(dir);
        }
    }

    [Fact]
    public void SkipsObservation_WhenEffectivePrefNonPositive()
    {
        var dir = Mithril.TestSupport.TestPaths.CreateTempDir("arwen_test");
        try
        {
            var (svc, _, inv) = BuildService(dir);

            // TestDislikedNecklace matches Yetta's Amulet (pref=2) and TestDislike (pref=-5) → net -3
            inv.Add(7777, "TestDislikedNecklace");
            svc.OnStartInteraction("NPC_Yetta");
            svc.OnItemDeleted(7777);
            svc.OnDeltaFavor("NPC_Yetta", 10); // would be positive but pref math is negative

            svc.Data.Observations.Should().BeEmpty();
        }
        finally
        {
            SafeDeleteDir(dir);
        }
    }

    [Fact]
    public void ExportImport_Roundtrips()
    {
        var dir1 = Mithril.TestSupport.TestPaths.CreateTempDir("arwen_test");
        var dir2 = Mithril.TestSupport.TestPaths.CreateTempDir("arwen_test");
        try
        {
            var (svc1, _, inv1) = BuildService(dir1);
            inv1.Add(100, "Moonstone");
            svc1.OnStartInteraction("NPC_Sanja");
            svc1.OnItemDeleted(100);
            svc1.OnDeltaFavor("NPC_Sanja", 22.5);

            var json = svc1.ExportJson("test contributor");

            var (svc2, _, inv2) = BuildService(dir2);
            var imported = svc2.ImportJson(json);
            imported.Should().Be(1);
            svc2.Data.Observations.Should().HaveCount(1);
            svc2.GetRate("Moonstone").Should().BeApproximately(0.15, 0.001);
        }
        finally
        {
            SafeDeleteDir(dir1);
            SafeDeleteDir(dir2);
        }
    }

    [Fact]
    public void Import_DeduplicatesObservations()
    {
        var dir = Mithril.TestSupport.TestPaths.CreateTempDir("arwen_test");
        try
        {
            var (svc, _, inv) = BuildService(dir);
            inv.Add(100, "Moonstone");
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
            SafeDeleteDir(dir);
        }
    }

    [Fact]
    public void MultipleObservations_AverageRate()
    {
        var dir = Mithril.TestSupport.TestPaths.CreateTempDir("arwen_test");
        try
        {
            var (svc, _, inv) = BuildService(dir);

            inv.Add(100, "Moonstone");
            svc.OnStartInteraction("NPC_Sanja");
            svc.OnItemDeleted(100);
            svc.OnDeltaFavor("NPC_Sanja", 22.5); // rate = 0.15

            inv.Add(101, "Moonstone");
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
            SafeDeleteDir(dir);
        }
    }

    [Fact]
    public void ExportCommunityJson_ContainsNoRawObservations()
    {
        var dir = Mithril.TestSupport.TestPaths.CreateTempDir("arwen_test");
        try
        {
            var (svc, _, inv) = BuildService(dir);
            inv.Add(100, "Moonstone");
            svc.OnStartInteraction("NPC_Sanja");
            svc.OnItemDeleted(100);
            svc.OnDeltaFavor("NPC_Sanja", 22.5);

            svc.Data.Observations.Should().NotBeEmpty();

            var json = svc.ExportCommunityJson("a note");

            json.Should().NotContain("observations", because: "community payload carries rates only");
            json.Should().NotContain("itemKeywords", because: "per-item keyword lists are observation-scoped");
            json.Should().NotContain("matchedPreferences", because: "matched preference lists are observation-scoped");
            json.Should().NotContain("derivedRate", because: "observation-scoped derived fields don't belong in aggregates");

            json.Should().Contain("\"schemaVersion\": 2",
                because: "wire schema is decoupled from local schema and pinned to what CommunityCalibrationService validates");
            json.Should().Contain("\"module\": \"arwen\"");
            json.Should().Contain("\"itemRates\"");
            json.Should().Contain("\"signatureRates\"");
            json.Should().Contain("\"npcRates\"");
            json.Should().Contain("\"keywordRates\"");
            json.Should().Contain("Moonstone");
        }
        finally
        {
            SafeDeleteDir(dir);
        }
    }

    [Fact]
    public void MigratesV1CalibrationFile_PopulatesKeywordsAndPreferences()
    {
        var dir = Mithril.TestSupport.TestPaths.CreateTempDir("arwen_test");
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
            var svc = new CalibrationService(refData, index, new FakeInventory(), dir);

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
            SafeDeleteDir(dir);
        }
    }

    [Fact]
    public void V3Migration_DropsStackableObservations_SetsQuantity1OnSurvivors_WritesBackup()
    {
        var dir = Mithril.TestSupport.TestPaths.CreateTempDir("arwen_test");
        try
        {
            // Synthesize a v2-shape file with one stackable (Phlogiston1) and one
            // non-stackable (Moonstone) observation. v2 had no Quantity field.
            var v2Path = Path.Combine(dir, "calibration.json");
            var v2Json = """
                {
                  "version": 2,
                  "observations": [
                    {
                      "npcKey": "NPC_Sanja",
                      "itemInternalName": "Moonstone",
                      "itemKeywords": ["Crystal", "Moonstone"],
                      "matchedPreferences": [
                        { "name": "Moonstones", "desire": "Love", "pref": 1.5, "keywords": ["Moonstone"] }
                      ],
                      "itemValue": 100,
                      "favorDelta": 22.5,
                      "timestamp": "2026-04-20T00:00:00+00:00"
                    },
                    {
                      "npcKey": "NPC_Sanja",
                      "itemInternalName": "Phlogiston1",
                      "itemKeywords": ["Crystal", "Moonstone"],
                      "matchedPreferences": [
                        { "name": "Moonstones", "desire": "Love", "pref": 1.5, "keywords": ["Moonstone"] }
                      ],
                      "itemValue": 5,
                      "favorDelta": 13.2,
                      "timestamp": "2026-04-20T00:00:00+00:00"
                    }
                  ]
                }
                """;
            File.WriteAllText(v2Path, v2Json);

            var refData = BuildRefData();
            var index = new GiftIndex();
            index.Build(refData.Items, refData.Npcs);
            var svc = new CalibrationService(refData, index, new FakeInventory(), dir);

            // Stackable observation dropped; non-stackable Moonstone survives with explicit Quantity=1.
            svc.Data.Observations.Should().HaveCount(1);
            var survivor = svc.Data.Observations[0];
            survivor.ItemInternalName.Should().Be("Moonstone");
            survivor.Quantity.Should().Be(1);

            // File rewritten at v3.
            svc.Data.Version.Should().Be(3);
            using var doc = JsonDocument.Parse(File.ReadAllBytes(v2Path));
            doc.RootElement.GetProperty("version").GetInt32().Should().Be(3);

            // Pre-migration backup exists.
            File.Exists(v2Path + ".v2.bak").Should().BeTrue();
        }
        finally
        {
            SafeDeleteDir(dir);
        }
    }

    [Fact]
    public void RecordObservation_StackableItem_KnownSize_RecordsWithCorrectQuantity()
    {
        var dir = Mithril.TestSupport.TestPaths.CreateTempDir("arwen_test");
        try
        {
            var (svc, _, inv) = BuildService(dir);

            // Phlogiston1 has MaxStackSize=10. Tracker knows the gifted stack was 5 units.
            inv.Add(9999, "Phlogiston1", stackSize: 5);
            svc.OnStartInteraction("NPC_Sanja");
            svc.OnItemDeleted(9999);
            svc.OnDeltaFavor("NPC_Sanja", 13.2);

            svc.Data.Observations.Should().HaveCount(1);
            var obs = svc.Data.Observations[0];
            obs.Quantity.Should().Be(5);
            obs.FavorDelta.Should().Be(13.2);
            // DerivedRate = 13.2 / (5 [value] * 1.5 [pref] * 5 [quantity]) = 0.352
            obs.DerivedRate.Should().BeApproximately(0.352, 0.001);
        }
        finally
        {
            SafeDeleteDir(dir);
        }
    }

    [Fact]
    public void RecordObservation_StackableItem_UnknownSize_GoesToPending_NotPersisted()
    {
        var dir = Mithril.TestSupport.TestPaths.CreateTempDir("arwen_test");
        try
        {
            var (svc, _, inv) = BuildService(dir);

            // Tracker resolves the InternalName but reports stack size = 0 (unknown),
            // simulating a carryover stack that's never seen an event this session.
            inv.Add(9999, "Phlogiston1", stackSize: 0);
            svc.OnStartInteraction("NPC_Sanja");
            svc.OnItemDeleted(9999);
            svc.OnDeltaFavor("NPC_Sanja", 13.2);

            svc.Data.Observations.Should().BeEmpty(
                because: "stackable gift with no tracker data — park for user confirmation rather than persist with unknown quantity");

            svc.PendingObservations.Should().ContainSingle();
            var pending = svc.PendingObservations[0];
            pending.NpcKey.Should().Be("NPC_Sanja");
            pending.InternalName.Should().Be("Phlogiston1");
            pending.FavorDelta.Should().Be(13.2);
            pending.MaxStackSize.Should().Be(10);
            pending.MatchedPreferences.Should().HaveCount(1);
        }
        finally
        {
            SafeDeleteDir(dir);
        }
    }

    [Fact]
    public void ConfirmPending_PromotesToObservationsWithSuppliedQuantity()
    {
        var dir = Mithril.TestSupport.TestPaths.CreateTempDir("arwen_test");
        try
        {
            var (svc, _, inv) = BuildService(dir);

            inv.Add(9999, "Phlogiston1", stackSize: 0); // unknown → pending
            svc.OnStartInteraction("NPC_Sanja");
            svc.OnItemDeleted(9999);
            svc.OnDeltaFavor("NPC_Sanja", 13.2);

            var pending = svc.PendingObservations.Single();
            var ok = svc.ConfirmPending(pending.Id, 5);
            ok.Should().BeTrue();

            svc.PendingObservations.Should().BeEmpty();
            svc.Data.Observations.Should().HaveCount(1);
            var obs = svc.Data.Observations[0];
            obs.Quantity.Should().Be(5);
            // DerivedRate = 13.2 / (5 [value] * 1.5 [pref] * 5 [quantity]) = 0.352
            obs.DerivedRate.Should().BeApproximately(0.352, 0.001);

            // Rate tables refreshed.
            svc.Data.ItemRates.Should().ContainKey("NPC_Sanja|Phlogiston1");
        }
        finally
        {
            SafeDeleteDir(dir);
        }
    }

    [Fact]
    public void ConfirmPending_QuantityOutOfRange_IsRejected()
    {
        var dir = Mithril.TestSupport.TestPaths.CreateTempDir("arwen_test");
        try
        {
            var (svc, _, inv) = BuildService(dir);

            inv.Add(9999, "Phlogiston1", stackSize: 0);
            svc.OnStartInteraction("NPC_Sanja");
            svc.OnItemDeleted(9999);
            svc.OnDeltaFavor("NPC_Sanja", 13.2);

            var pending = svc.PendingObservations.Single();

            svc.ConfirmPending(pending.Id, 0).Should().BeFalse();
            svc.ConfirmPending(pending.Id, 11).Should().BeFalse(); // MaxStackSize == 10

            svc.PendingObservations.Should().ContainSingle(
                because: "rejected confirms must leave the pending entry untouched");
            svc.Data.Observations.Should().BeEmpty();
        }
        finally
        {
            SafeDeleteDir(dir);
        }
    }

    [Fact]
    public void ConfirmPending_UnknownId_ReturnsFalse()
    {
        var dir = Mithril.TestSupport.TestPaths.CreateTempDir("arwen_test");
        try
        {
            var (svc, _, _) = BuildService(dir);
            svc.ConfirmPending(Guid.NewGuid(), 5).Should().BeFalse();
        }
        finally
        {
            SafeDeleteDir(dir);
        }
    }

    [Fact]
    public void DiscardPending_RemovesWithoutPersisting()
    {
        var dir = Mithril.TestSupport.TestPaths.CreateTempDir("arwen_test");
        try
        {
            var (svc, _, inv) = BuildService(dir);

            inv.Add(9999, "Phlogiston1", stackSize: 0);
            svc.OnStartInteraction("NPC_Sanja");
            svc.OnItemDeleted(9999);
            svc.OnDeltaFavor("NPC_Sanja", 13.2);

            var pending = svc.PendingObservations.Single();
            svc.DiscardPending(pending.Id).Should().BeTrue();

            svc.PendingObservations.Should().BeEmpty();
            svc.Data.Observations.Should().BeEmpty();
        }
        finally
        {
            SafeDeleteDir(dir);
        }
    }

    [Fact]
    public void PendingChanged_FiresOnEnqueueAndConfirm()
    {
        var dir = Mithril.TestSupport.TestPaths.CreateTempDir("arwen_test");
        try
        {
            var (svc, _, inv) = BuildService(dir);
            var fires = 0;
            svc.PendingChanged += (_, _) => fires++;

            inv.Add(9999, "Phlogiston1", stackSize: 0);
            svc.OnStartInteraction("NPC_Sanja");
            svc.OnItemDeleted(9999);
            svc.OnDeltaFavor("NPC_Sanja", 13.2);

            fires.Should().BeGreaterThanOrEqualTo(1, "enqueue must notify subscribers");
            var firesAfterEnqueue = fires;

            var pending = svc.PendingObservations.Single();
            svc.ConfirmPending(pending.Id, 5);

            fires.Should().BeGreaterThan(firesAfterEnqueue, "confirm must notify subscribers via the underlying CollectionChanged");
        }
        finally
        {
            SafeDeleteDir(dir);
        }
    }

    [Fact]
    public void RecordObservation_NonStackableItem_RecordsRegardlessOfTracker()
    {
        var dir = Mithril.TestSupport.TestPaths.CreateTempDir("arwen_test");
        try
        {
            var (svc, _, inv) = BuildService(dir);

            // Non-stackable item (MaxStackSize == 1): tracker isn't consulted at all
            // because PG always gifts exactly 1 unit. Set stackSize=0 to prove it's
            // ignored for non-stackables.
            inv.Add(12345, "Moonstone", stackSize: 0);
            svc.OnStartInteraction("NPC_Sanja");
            svc.OnItemDeleted(12345);
            svc.OnDeltaFavor("NPC_Sanja", 22.5);

            svc.Data.Observations.Should().HaveCount(1);
            var obs = svc.Data.Observations[0];
            obs.Quantity.Should().Be(1);
        }
        finally
        {
            SafeDeleteDir(dir);
        }
    }

    [Fact]
    public void DerivedRate_DividesByQuantity()
    {
        var obs = new GiftObservation
        {
            FavorDelta = 10,
            ItemValue = 5,
            Quantity = 2,
            MatchedPreferences = [new MatchedPreference { Name = "Test", Pref = 1, Keywords = ["x"] }],
        };

        // 10 / (5 * 1 * 2) = 1.0
        obs.DerivedRate.Should().Be(1.0);

        // Sanity: same observation with Quantity=1 doubles the rate.
        obs.Quantity = 1;
        obs.DerivedRate.Should().Be(2.0);
    }

    // ── Split-storage migration ───────────────────────────────────────

    [Fact]
    public void SplitMigration_LegacySingleFile_LiftsObservationsAndWritesBackup()
    {
        var dir = Mithril.TestSupport.TestPaths.CreateTempDir("arwen_test");
        try
        {
            // Legacy v3 single-file calibration.json (post-v3 migration, pre-split layout).
            var legacyPath = Path.Combine(dir, "calibration.json");
            var observationsPath = Path.Combine(dir, "observations.json");
            var legacyJson = """
                {
                  "version": 3,
                  "observations": [
                    {
                      "npcKey": "NPC_Sanja",
                      "itemInternalName": "Moonstone",
                      "itemKeywords": ["Crystal", "Moonstone"],
                      "matchedPreferences": [
                        { "name": "Moonstones", "desire": "Love", "pref": 1.5, "keywords": ["Moonstone"] }
                      ],
                      "itemValue": 100,
                      "favorDelta": 22.5,
                      "quantity": 1,
                      "timestamp": "2026-04-20T00:00:00+00:00"
                    }
                  ]
                }
                """;
            File.WriteAllText(legacyPath, legacyJson);
            File.Exists(observationsPath).Should().BeFalse("pre-split state has only the legacy file");

            var (svc, _, _) = BuildService(dir);

            svc.Data.Observations.Should().HaveCount(1, "the lone legacy observation lands in memory");
            svc.Data.Observations[0].ItemInternalName.Should().Be("Moonstone");

            // Layout migration outputs: observations.json populated, calibration.json
            // rewritten without an `observations` array, and a one-shot .split.bak.
            File.Exists(observationsPath).Should().BeTrue();
            using (var obsDoc = JsonDocument.Parse(File.ReadAllBytes(observationsPath)))
            {
                obsDoc.RootElement.GetProperty("observations").GetArrayLength().Should().Be(1);
            }
            using (var aggDoc = JsonDocument.Parse(File.ReadAllBytes(legacyPath)))
            {
                aggDoc.RootElement.TryGetProperty("observations", out _).Should().BeFalse(
                    "calibration.json is now AggregatesData (rates only)");
                aggDoc.RootElement.GetProperty("itemRates").EnumerateObject().Should().NotBeEmpty();
            }
            File.Exists(legacyPath + ".split.bak").Should().BeTrue("one-shot pre-split snapshot");
        }
        finally
        {
            SafeDeleteDir(dir);
        }
    }

    [Fact]
    public void SplitMigration_FreshInstall_NoFilesNoBackup()
    {
        var dir = Mithril.TestSupport.TestPaths.CreateTempDir("arwen_test");
        try
        {
            var (svc, _, _) = BuildService(dir);

            svc.Data.Observations.Should().BeEmpty();
            File.Exists(Path.Combine(dir, "calibration.json")).Should().BeFalse("nothing to save until first observation lands");
            File.Exists(Path.Combine(dir, "observations.json")).Should().BeFalse();
            File.Exists(Path.Combine(dir, "calibration.json.split.bak")).Should().BeFalse(
                "no split.bak when there was no legacy file to begin with");
        }
        finally
        {
            SafeDeleteDir(dir);
        }
    }

    [Fact]
    public void SplitMigration_PostSplitState_NoSecondBackup()
    {
        // Two startups in a row on a post-split build. The second startup must NOT
        // create another .split.bak — the first one already captured the original
        // legacy file, and re-snapshotting the new layout would be a double-write.
        var dir = Mithril.TestSupport.TestPaths.CreateTempDir("arwen_test");
        try
        {
            // Startup 1: legacy file → split migration runs.
            var legacyPath = Path.Combine(dir, "calibration.json");
            File.WriteAllText(legacyPath, """
                {
                  "version": 3,
                  "observations": [
                    {
                      "npcKey": "NPC_Sanja",
                      "itemInternalName": "Moonstone",
                      "itemKeywords": ["Crystal", "Moonstone"],
                      "matchedPreferences": [
                        { "name": "Moonstones", "desire": "Love", "pref": 1.5, "keywords": ["Moonstone"] }
                      ],
                      "itemValue": 100,
                      "favorDelta": 22.5,
                      "quantity": 1,
                      "timestamp": "2026-04-20T00:00:00+00:00"
                    }
                  ]
                }
                """);
            BuildService(dir);
            var splitBakBytes = File.ReadAllBytes(legacyPath + ".split.bak");

            // Startup 2: post-split files exist; .split.bak must be untouched.
            BuildService(dir);
            File.ReadAllBytes(legacyPath + ".split.bak").Should().Equal(splitBakBytes,
                "BackupBeforeSplit is one-shot and idempotent");
        }
        finally
        {
            SafeDeleteDir(dir);
        }
    }

    [Fact]
    public void SplitMigration_DowngradeThenUpgrade_MergesWithDedup()
    {
        // A user runs the post-split build (writes observations.json with obs A, B),
        // downgrades to a pre-split build (rewrites legacy calibration.json with
        // its current observations: A + a NEW C the user gifted on the old build),
        // then upgrades again. Both files now carry observations; we must MERGE
        // (dedup A) rather than picking one and dropping the other's unique entries.
        var dir = Mithril.TestSupport.TestPaths.CreateTempDir("arwen_test");
        try
        {
            // Shared timestamps so ObservationKey can dedup deterministically.
            var tsA = "2026-04-20T00:00:00+00:00";
            var tsB = "2026-04-21T00:00:00+00:00";
            var tsC = "2026-04-22T00:00:00+00:00";
            var prefBlock = """
                "matchedPreferences": [
                  { "name": "Moonstones", "desire": "Love", "pref": 1.5, "keywords": ["Moonstone"] }
                ]
                """;
            var keywordsBlock = """ "itemKeywords": ["Crystal", "Moonstone"] """;

            string ObsJson(double favorDelta, string ts) => $$"""
                {
                  "npcKey": "NPC_Sanja",
                  "itemInternalName": "Moonstone",
                  {{keywordsBlock}},
                  {{prefBlock}},
                  "itemValue": 100,
                  "favorDelta": {{favorDelta}},
                  "quantity": 1,
                  "timestamp": "{{ts}}"
                }
                """;

            // observations.json: post-split layout, contains A and B.
            File.WriteAllText(Path.Combine(dir, "observations.json"), $$"""
                {
                  "version": 3,
                  "observations": [
                    {{ObsJson(22.5, tsA)}},
                    {{ObsJson(15.0, tsB)}}
                  ]
                }
                """);

            // calibration.json: legacy single-file shape rewritten by a downgraded
            // build. Contains A (duplicate) and C (unique).
            File.WriteAllText(Path.Combine(dir, "calibration.json"), $$"""
                {
                  "version": 3,
                  "observations": [
                    {{ObsJson(22.5, tsA)}},
                    {{ObsJson(30.0, tsC)}}
                  ]
                }
                """);

            var (svc, _, _) = BuildService(dir);

            svc.Data.Observations.Should().HaveCount(3, "A is deduped, B and C are unique");
            var deltas = svc.Data.Observations.Select(o => o.FavorDelta).OrderBy(d => d).ToArray();
            deltas.Should().Equal(15.0, 22.5, 30.0);
        }
        finally
        {
            SafeDeleteDir(dir);
        }
    }

    [Fact]
    public void SplitMigration_V2OnDisk_BothBackupsCoexist()
    {
        // A v2-on-disk user upgrading to a post-split build: BOTH backups should be
        // created. .v2.bak from the version-ladder migration (orthogonal axis), and
        // .split.bak from the layout split (also orthogonal axis).
        var dir = Mithril.TestSupport.TestPaths.CreateTempDir("arwen_test");
        try
        {
            var legacyPath = Path.Combine(dir, "calibration.json");
            File.WriteAllText(legacyPath, """
                {
                  "version": 2,
                  "observations": [
                    {
                      "npcKey": "NPC_Sanja",
                      "itemInternalName": "Moonstone",
                      "itemKeywords": ["Crystal", "Moonstone"],
                      "matchedPreferences": [
                        { "name": "Moonstones", "desire": "Love", "pref": 1.5, "keywords": ["Moonstone"] }
                      ],
                      "itemValue": 100,
                      "favorDelta": 22.5,
                      "timestamp": "2026-04-20T00:00:00+00:00"
                    }
                  ]
                }
                """);

            BuildService(dir);

            File.Exists(legacyPath + ".v2.bak").Should().BeTrue("version migration backup");
            File.Exists(legacyPath + ".split.bak").Should().BeTrue("layout split backup");
        }
        finally
        {
            SafeDeleteDir(dir);
        }
    }

    [Fact]
    public void ImportJson_RejectsWireFormatPayload_NoStateMutation()
    {
        // GiftRatesPayload (community wire format) and CalibrationData (full local
        // export) share rate-dict property names. Without a sanity check, a wire
        // payload would partially deserialize as CalibrationData with empty
        // observations and populated aggregates — and silently flow through the
        // migration ladder doing nothing. The behavioral guarantee is "no mutation":
        // the import must not corrupt existing state nor add 0 observations.
        var dir = Mithril.TestSupport.TestPaths.CreateTempDir("arwen_test");
        try
        {
            var (svc, _, inv) = BuildService(dir);

            // Seed one real observation so "no mutation" is observable.
            inv.Add(100, "Moonstone");
            svc.OnStartInteraction("NPC_Sanja");
            svc.OnItemDeleted(100);
            svc.OnDeltaFavor("NPC_Sanja", 22.5);
            svc.Data.Observations.Should().HaveCount(1);

            // Wire-format payload as it would arrive from a community-share GitHub issue.
            var wireJson = """
                {
                  "schemaVersion": 2,
                  "module": "arwen",
                  "exportedAt": "2026-04-25T06:35:38.27+00:00",
                  "attributionOptOut": false,
                  "itemRates": {
                    "NPC_Larsan|Topaz": { "rate": 0.5, "sampleCount": 1, "minRate": 0.5, "maxRate": 0.5 }
                  },
                  "signatureRates": {},
                  "npcRates": {},
                  "keywordRates": {}
                }
                """;

            var added = svc.ImportJson(wireJson);
            added.Should().Be(0, "wire payload carries no observations to add");
            svc.Data.Observations.Should().HaveCount(1, "existing observation must remain unchanged");
            svc.Data.Observations[0].ItemInternalName.Should().Be("Moonstone");
        }
        finally
        {
            SafeDeleteDir(dir);
        }
    }

    [Fact]
    public void Load_CorruptObservationsJson_QuarantinesAndDoesNotOverwrite()
    {
        // If observations.json exists but can't be parsed, the service must rename
        // it to .corrupt.bak instead of treating it as absent. Without this, the next
        // Save() (triggered by the next observation, or by any layout migration) would
        // silently overwrite the unparseable file with empty data — silent loss of
        // whatever the user had before.
        var dir = Mithril.TestSupport.TestPaths.CreateTempDir("arwen_test");
        try
        {
            var observationsPath = Path.Combine(dir, "observations.json");
            File.WriteAllText(observationsPath, "{ not valid JSON ::");
            var corruptPath = observationsPath + ".corrupt.bak";

            var (svc, _, _) = BuildService(dir);

            svc.Data.Observations.Should().BeEmpty();
            File.Exists(corruptPath).Should().BeTrue(
                "the corrupt file is preserved for forensics, not silently overwritten");
            File.ReadAllText(corruptPath).Should().Contain("not valid JSON",
                "the corrupt file's contents must be the original bytes");
        }
        finally
        {
            SafeDeleteDir(dir);
        }
    }
}
