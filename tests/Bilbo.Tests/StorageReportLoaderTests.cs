using System.IO;
using System.Text.Json;
using Bilbo.Domain;
using Bilbo.Services;
using Mithril.Shared.Reference;
using Mithril.Shared.Storage;
using FluentAssertions;
using Xunit;

namespace Bilbo.Tests;

public class StorageReportLoaderTests
{
    [Fact]
    public void Load_DeserializesRoundTrip()
    {
        // Arrange: build a minimal report, serialize, write to temp file
        var report = new StorageReport(
            "TestChar",
            "TestServer",
            "2026-04-18 13:27:12Z",
            "Storage",
            1,
            [
                new StorageItem(
                    TypeID: 44345,
                    Name: "James Eltibule's Helm of Lycanthropy",
                    StackSize: 1,
                    Value: 324,
                    StorageVault: "CouncilVault",
                    Rarity: "Exceptional",
                    Slot: "Head",
                    Level: 30,
                    IsInInventory: false,
                    IsCrafted: false,
                    AttunedTo: null,
                    Crafter: null,
                    Durability: null,
                    TransmuteCount: null,
                    CraftPoints: null,
                    TSysPowers: [new TsysPower(3, "WerewolfBoost"), new TsysPower(6, "SanguineFangsBoost")],
                    TSysImbuePower: null,
                    TSysImbuePowerTier: null,
                    PetHusbandryState: "Unregistered"),
                new StorageItem(
                    TypeID: 5020,
                    Name: "Mutton",
                    StackSize: 61,
                    Value: 15,
                    StorageVault: "Saddlebag",
                    Rarity: null,
                    Slot: null,
                    Level: null,
                    IsInInventory: false,
                    IsCrafted: false,
                    AttunedTo: null,
                    Crafter: null,
                    Durability: null,
                    TransmuteCount: null,
                    CraftPoints: null,
                    TSysPowers: null,
                    TSysImbuePower: null,
                    TSysImbuePowerTier: null,
                    PetHusbandryState: "Unregistered"),
            ]);

        var json = JsonSerializer.Serialize(report, StorageReportJsonContext.Default.StorageReport);
        var tempFile = Path.GetTempFileName();
        try
        {
            File.WriteAllText(tempFile, json);

            // Act
            var loaded = StorageReportLoader.Load(tempFile);

            // Assert
            loaded.Character.Should().Be("TestChar");
            loaded.ServerName.Should().Be("TestServer");
            loaded.Items.Should().HaveCount(2);

            loaded.Items[0].Name.Should().Be("James Eltibule's Helm of Lycanthropy");
            loaded.Items[0].TSysPowers.Should().HaveCount(2);
            loaded.Items[0].Rarity.Should().Be("Exceptional");

            loaded.Items[1].Name.Should().Be("Mutton");
            loaded.Items[1].StackSize.Should().Be(61);
            loaded.Items[1].TSysPowers.Should().BeNull();
        }
        finally
        {
            File.Delete(tempFile);
        }
    }

    [Fact]
    public void ToRows_FlattensCorrectly()
    {
        var report = new StorageReport("X", "Y", "", "Storage", 1,
        [
            new StorageItem(1, "Sword", 1, 100, "NPC_JaceSoral", "Epic", "MainHand", 30,
                false, false, null, null, null, null, null,
                [new TsysPower(6, "Boost1"), new TsysPower(6, "Boost2"), new TsysPower(6, "Boost3")],
                null, null, null),
            new StorageItem(2, "Apple", 10, 5, null, null, null, null,
                true, false, null, null, null, null, null, null, null, null, null),
        ]);

        var rows = StorageRowMapper.ToRows(report, new EmptyRefData());

        rows.Should().HaveCount(2);

        rows[0].Name.Should().Be("Sword");
        rows[0].Location.Should().Be("NPC: Jace Soral");
        rows[0].TotalValue.Should().Be(100);
        rows[0].ModCount.Should().Be(3);

        rows[1].Name.Should().Be("Apple");
        rows[1].Location.Should().Be("Inventory");
        rows[1].TotalValue.Should().Be(50);
        rows[1].ModCount.Should().Be(0);
    }

    [Fact]
    public void NormalizeLocation_HandlesAllPatterns()
    {
        StorageReportLoader.NormalizeLocation("CouncilVault", false).Should().Be("Council Vault");
        StorageReportLoader.NormalizeLocation("StorageCrate", false).Should().Be("Storage Crate");
        StorageReportLoader.NormalizeLocation("NPC_JaceSoral", false).Should().Be("NPC: Jace Soral");
        StorageReportLoader.NormalizeLocation("NPC_Joe", false).Should().Be("NPC: Joe");
        StorageReportLoader.NormalizeLocation("NPC_MarithFelgard", false).Should().Be("NPC: Marith Felgard");
        StorageReportLoader.NormalizeLocation("*AccountStorage_Serbule", false).Should().Be("Account: Serbule");
        StorageReportLoader.NormalizeLocation("Saddlebag", false).Should().Be("Saddlebag");
        StorageReportLoader.NormalizeLocation(null, true).Should().Be("Inventory");
        StorageReportLoader.NormalizeLocation(null, false).Should().Be("Inventory");
    }

    private sealed class EmptyRefData : IReferenceDataService
    {
        public IReadOnlyList<string> Keys { get; } = [];
        public IReadOnlyDictionary<long, ItemEntry> Items { get; } = new Dictionary<long, ItemEntry>();
        public IReadOnlyDictionary<string, ItemEntry> ItemsByInternalName { get; } = new Dictionary<string, ItemEntry>();
        public ItemKeywordIndex KeywordIndex { get; } = ItemKeywordIndex.Empty;
        public IReadOnlyDictionary<string, RecipeEntry> Recipes { get; } = new Dictionary<string, RecipeEntry>();
        public IReadOnlyDictionary<string, RecipeEntry> RecipesByInternalName { get; } = new Dictionary<string, RecipeEntry>();
        public IReadOnlyDictionary<string, SkillEntry> Skills { get; } = new Dictionary<string, SkillEntry>();
        public IReadOnlyDictionary<string, XpTableEntry> XpTables { get; } = new Dictionary<string, XpTableEntry>();
        public IReadOnlyDictionary<string, NpcEntry> Npcs { get; } = new Dictionary<string, NpcEntry>();
        public IReadOnlyDictionary<string, IReadOnlyList<ItemSource>> ItemSources { get; } = new Dictionary<string, IReadOnlyList<ItemSource>>();
        public IReadOnlyDictionary<string, AttributeEntry> Attributes { get; } = new Dictionary<string, AttributeEntry>();
        public IReadOnlyDictionary<string, PowerEntry> Powers { get; } = new Dictionary<string, PowerEntry>();
        public IReadOnlyDictionary<string, IReadOnlyList<string>> Profiles { get; } = new Dictionary<string, IReadOnlyList<string>>();
        public ReferenceFileSnapshot GetSnapshot(string key) => new(key, ReferenceFileSource.Bundled, "v469", null, 0);
        public Task RefreshAsync(string key, CancellationToken ct = default) => Task.CompletedTask;
        public Task RefreshAllAsync(CancellationToken ct = default) => Task.CompletedTask;
        public void BeginBackgroundRefresh() { }
        public event EventHandler<string>? FileUpdated { add { } remove { } }
    }
}
