using System.IO;
using System.Text.Json;
using FluentAssertions;
using Mithril.Shared.Character;
using Saruman.State;
using Xunit;

namespace Saruman.Tests.State;

[Collection("FileIO")]
public sealed class SarumanCodebookLegacyMigrationTests : IDisposable
{
    private readonly string _root;

    public SarumanCodebookLegacyMigrationTests()
    {
        _root = Mithril.TestSupport.TestPaths.CreateTempDir("saruman-legacy");
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { }
    }

    [Fact]
    public void NoFiles_ReturnsFalse()
    {
        var migration = new SarumanCodebookLegacyMigration(_root);

        var result = migration.TryMigrate("Arthur", "Kwatoxi", out _, out _);

        result.Should().BeFalse();
    }

    [Fact]
    public void V1SarumanJson_MigratesCodebook()
    {
        WriteV1Saruman("Arthur", "Kwatoxi", new
        {
            schemaVersion = 1,
            codebook = new Dictionary<string, object>
            {
                ["CHUCKMRYJ"] = new { code = "CHUCKMRYJ", effectName = "Fast Swimmer", description = "Swim fast!", firstDiscoveredAt = "2026-04-23T10:26:56Z", discoveryCount = 1, state = 0 },
                ["VYLLTRIS"] = new { code = "VYLLTRIS", effectName = "Teleport", description = "Teleport somewhere.", firstDiscoveredAt = "2026-04-23T14:13:52Z", discoveryCount = 1, state = 1, spentAt = "2026-04-24T03:54:41Z" },
            }
        });

        var migration = new SarumanCodebookLegacyMigration(_root);
        var result = migration.TryMigrate("Arthur", "Kwatoxi", out var migrated, out _);

        result.Should().BeTrue();
        migrated.Entries.Should().HaveCount(2);

        migrated.Entries["CHUCKMRYJ"].Effect.Should().Be("Fast Swimmer");
        migrated.Entries["CHUCKMRYJ"].LastSpentAt.Should().BeNull();

        migrated.Entries["VYLLTRIS"].Effect.Should().Be("Teleport");
        migrated.Entries["VYLLTRIS"].LastSpentAt.Should().NotBeNull();
        migrated.Entries["VYLLTRIS"].LastSpentAt!.Value.Year.Should().Be(2026);
    }

    [Fact]
    public void V1SarumanJson_SkipsIfAlreadyV2()
    {
        WriteV1Saruman("Arthur", "Kwatoxi", new
        {
            schemaVersion = 2,
            spentOverrides = Array.Empty<string>(),
        });

        var migration = new SarumanCodebookLegacyMigration(_root);
        var result = migration.TryMigrate("Arthur", "Kwatoxi", out _, out _);

        result.Should().BeFalse();
    }

    [Fact]
    public void SplitFiles_DiscoveryOnly_MigratesWithNoSpent()
    {
        WriteSplitDiscovery("Arthur", "Kwatoxi", new
        {
            schemaVersion = 1,
            discoveries = new Dictionary<string, object>
            {
                ["TESTCODE"] = new { code = "TESTCODE", effectName = "Fast Swim", description = "Go fast", discoveredAt = "2026-01-15T10:00:00+00:00" },
            }
        });

        var migration = new SarumanCodebookLegacyMigration(_root);
        var result = migration.TryMigrate("Arthur", "Kwatoxi", out var migrated, out _);

        result.Should().BeTrue();
        migrated.Entries["TESTCODE"].Effect.Should().Be("Fast Swim");
        migrated.Entries["TESTCODE"].LastSpentAt.Should().BeNull();
    }

    [Fact]
    public void SplitFiles_DiscoveryAndSpent_MergesCorrectly()
    {
        WriteSplitDiscovery("Arthur", "Kwatoxi", new
        {
            schemaVersion = 1,
            discoveries = new Dictionary<string, object>
            {
                ["KNOWNCODE"] = new { code = "KNOWNCODE", effectName = "Anemia", description = "Bad effect", discoveredAt = "2026-01-10T12:00:00+00:00" },
                ["SPENTCODE"] = new { code = "SPENTCODE", effectName = "Run Fast", description = "", discoveredAt = "2026-01-11T14:00:00+00:00" },
            }
        });

        WriteSplitSpent("Arthur", "Kwatoxi", new
        {
            schemaVersion = 1,
            spentAt = new Dictionary<string, string>
            {
                ["SPENTCODE"] = "2026-01-12T09:30:00",
            }
        });

        var migration = new SarumanCodebookLegacyMigration(_root);
        var result = migration.TryMigrate("Arthur", "Kwatoxi", out var migrated, out _);

        result.Should().BeTrue();
        migrated.Entries["KNOWNCODE"].LastSpentAt.Should().BeNull();
        migrated.Entries["SPENTCODE"].LastSpentAt.Should().NotBeNull();
    }

    [Fact]
    public void V1SarumanJson_TakesPriority_OverSplitFiles()
    {
        WriteV1Saruman("Arthur", "Kwatoxi", new
        {
            schemaVersion = 1,
            codebook = new Dictionary<string, object>
            {
                ["FROMV1"] = new { code = "FROMV1", effectName = "V1 Source", description = "", firstDiscoveredAt = "2026-04-01T00:00:00Z", discoveryCount = 1, state = 0 },
            }
        });
        WriteSplitDiscovery("Arthur", "Kwatoxi", new
        {
            schemaVersion = 1,
            discoveries = new Dictionary<string, object>
            {
                ["FROMSPLIT"] = new { code = "FROMSPLIT", effectName = "Split Source", description = "", discoveredAt = "2026-05-01T00:00:00+00:00" },
            }
        });

        var migration = new SarumanCodebookLegacyMigration(_root);
        var result = migration.TryMigrate("Arthur", "Kwatoxi", out var migrated, out _);

        result.Should().BeTrue();
        migrated.Entries.Should().ContainKey("FROMV1");
        migrated.Entries.Should().NotContainKey("FROMSPLIT");
    }

    private void WriteV1Saruman(string character, string server, object data)
    {
        var dir = Path.Combine(_root, PerCharacterStore<SarumanCodebook>.Slug(character, server));
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "saruman.json"), Serialize(data));
    }

    private void WriteSplitDiscovery(string character, string server, object data)
    {
        var dir = Path.Combine(_root, PerCharacterStore<SarumanCodebook>.Slug(character, server));
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "wop-discovery.json"), Serialize(data));
    }

    private void WriteSplitSpent(string character, string server, object data)
    {
        var dir = Path.Combine(_root, PerCharacterStore<SarumanCodebook>.Slug(character, server));
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "wop-spent.json"), Serialize(data));
    }

    private static string Serialize(object data) => JsonSerializer.Serialize(data, new JsonSerializerOptions
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
    });
}
