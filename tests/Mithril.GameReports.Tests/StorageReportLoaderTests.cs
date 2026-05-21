using System.IO;
using System.Text.Json;
using FluentAssertions;
using Mithril.TestSupport;
using Xunit;

namespace Mithril.GameReports.Tests;

[Trait("Category", "FileIO")]
public sealed class StorageReportLoaderTests
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

    [Fact]
    public void ScanForReports_ParsesFileNamePattern_AndSortsByMtime()
    {
        var dir = TestPaths.CreateTempDir("gamereports-scan");
        try
        {
            // Three reports — newest one should land first.
            var older = Path.Combine(dir, "Alice_Alpha_items_20260101_120000.json");
            File.WriteAllText(older, "{}");
            File.SetLastWriteTimeUtc(older, DateTime.UtcNow.AddDays(-2));

            var middle = Path.Combine(dir, "Bob_Beta_items_20260102_120000.json");
            File.WriteAllText(middle, "{}");
            File.SetLastWriteTimeUtc(middle, DateTime.UtcNow.AddDays(-1));

            var newest = Path.Combine(dir, "Charlie_Gamma_items_20260103_120000.json");
            File.WriteAllText(newest, "{}");
            File.SetLastWriteTimeUtc(newest, DateTime.UtcNow);

            // Non-matching pattern is ignored.
            File.WriteAllText(Path.Combine(dir, "Character_Alice_Alpha.json"), "{}");
            File.WriteAllText(Path.Combine(dir, "random.json"), "{}");

            var results = StorageReportLoader.ScanForReports(dir);

            results.Should().HaveCount(3);
            results[0].Character.Should().Be("Charlie");
            results[0].Server.Should().Be("Gamma");
            results[1].Character.Should().Be("Bob");
            results[2].Character.Should().Be("Alice");
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void ScanForReports_OnMissingDirectory_ReturnsEmpty()
    {
        StorageReportLoader.ScanForReports(Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString()))
            .Should().BeEmpty();
    }
}
