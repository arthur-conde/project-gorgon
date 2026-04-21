using System.IO;
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
        };
        var npcs = new Dictionary<string, NpcEntry>(StringComparer.Ordinal)
        {
            ["NPC_Sanja"] = new("NPC_Sanja", "Sanja", "Serbule",
                [new NpcPreference("Love", ["Moonstone"], "Moonstones", 1.5, null)],
                ["Friends"]),
            ["NPC_Test"] = new("NPC_Test", "Test", "Serbule",
                [new NpcPreference("Love", ["Fruit"], "Fruit", 2.0, null)],
                ["Friends"]),
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
    public void DetectsGiftAndComputesRate()
    {
        var dir = Path.Combine(Path.GetTempPath(), $"arwen_test_{Guid.NewGuid():N}");
        try
        {
            var (svc, _) = BuildService(dir);

            // Simulate: player picks up moonstone, talks to Sanja, gifts it, gets favor
            svc.OnItemAdded("Moonstone", 12345);
            svc.OnStartInteraction("NPC_Sanja");
            svc.OnItemDeleted(12345);
            svc.OnDeltaFavor("NPC_Sanja", 22.5);

            svc.Data.Observations.Should().HaveCount(1);
            var obs = svc.Data.Observations[0];
            obs.NpcKey.Should().Be("NPC_Sanja");
            obs.ItemInternalName.Should().Be("Moonstone");
            obs.FavorDelta.Should().Be(22.5);
            obs.MatchedKeyword.Should().Be("Moonstone");

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
    public void EstimateFavor_UsesCalibrated_Rate()
    {
        var dir = Path.Combine(Path.GetTempPath(), $"arwen_test_{Guid.NewGuid():N}");
        try
        {
            var (svc, index) = BuildService(dir);

            // Calibrate the Moonstone category
            svc.OnItemAdded("Moonstone", 100);
            svc.OnStartInteraction("NPC_Sanja");
            svc.OnItemDeleted(100);
            svc.OnDeltaFavor("NPC_Sanja", 22.5);

            // Now estimate for another moonstone gift
            var match = index.MatchItemToNpc(1, "NPC_Sanja");
            match.Should().NotBeNull();
            var est = svc.EstimateFavor(match!);
            est.Should().BeApproximately(22.5, 0.01); // 1.5 * 100 * 0.15
        }
        finally
        {
            if (Directory.Exists(dir)) Directory.Delete(dir, true);
        }
    }

    [Fact]
    public void DetectsGift_DeltaBeforeDelete()
    {
        // Order B: game emits DeltaFavor before DeleteItem
        var dir = Path.Combine(Path.GetTempPath(), $"arwen_test_{Guid.NewGuid():N}");
        try
        {
            var (svc, _) = BuildService(dir);

            svc.OnItemAdded("Moonstone", 12345);
            svc.OnStartInteraction("NPC_Sanja");
            // Delta comes FIRST, then delete
            svc.OnDeltaFavor("NPC_Sanja", 22.5);
            svc.OnItemDeleted(12345);

            svc.Data.Observations.Should().HaveCount(1);
            var obs = svc.Data.Observations[0];
            obs.NpcKey.Should().Be("NPC_Sanja");
            obs.ItemInternalName.Should().Be("Moonstone");
            obs.FavorDelta.Should().Be(22.5);
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
            // No OnStartInteraction — not talking to NPC
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
            svc.OnDeltaFavor("NPC_Sanja", -10); // hate gift

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

            // Import into fresh service
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
            var imported = svc.ImportJson(json); // import same data again
            imported.Should().Be(0); // all duplicates
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

            // Two observations with slightly different rates
            svc.OnItemAdded("Moonstone", 100);
            svc.OnStartInteraction("NPC_Sanja");
            svc.OnItemDeleted(100);
            svc.OnDeltaFavor("NPC_Sanja", 22.5); // rate = 0.15

            svc.OnItemAdded("Moonstone", 101);
            svc.OnStartInteraction("NPC_Sanja");
            svc.OnItemDeleted(101);
            svc.OnDeltaFavor("NPC_Sanja", 24.0); // rate = 0.16

            var rate = svc.GetRate("Moonstone");
            rate.Should().BeApproximately(0.155, 0.001); // average of 0.15 and 0.16

            var categoryRate = svc.Data.Rates["Moonstone"];
            categoryRate.SampleCount.Should().Be(2);
            categoryRate.MinRate.Should().BeApproximately(0.15, 0.001);
            categoryRate.MaxRate.Should().BeApproximately(0.16, 0.001);
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
        public ReferenceFileSnapshot GetSnapshot(string key) => new(key, ReferenceFileSource.Bundled, "test", null, 0);
        public Task RefreshAsync(string key, CancellationToken ct = default) => Task.CompletedTask;
        public Task RefreshAllAsync(CancellationToken ct = default) => Task.CompletedTask;
        public void BeginBackgroundRefresh() { }
        public event EventHandler<string>? FileUpdated { add { } remove { } }
    }
}
