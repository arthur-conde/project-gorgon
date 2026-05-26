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
    public void DiscoveryOnly_MigratesWithNoSpent()
    {
        WriteDiscovery("Arthur", "Kwatoxi", new
        {
            schemaVersion = 1,
            discoveries = new Dictionary<string, object>
            {
                ["TESTCODE"] = new { code = "TESTCODE", effectName = "Fast Swim", description = "Go fast", discoveredAt = "2026-01-15T10:00:00+00:00" },
                ["ANOTHER"] = new { code = "ANOTHER", effectName = "Jump High", description = (string?)null, discoveredAt = "2026-02-20T08:30:00+00:00" },
            }
        });

        var migration = new SarumanCodebookLegacyMigration(_root);
        var result = migration.TryMigrate("Arthur", "Kwatoxi", out var migrated, out var legacyPath);

        result.Should().BeTrue();
        legacyPath.Should().NotBeNullOrEmpty();
        migrated.Entries.Should().HaveCount(2);

        migrated.Entries["TESTCODE"].Effect.Should().Be("Fast Swim");
        migrated.Entries["TESTCODE"].Description.Should().Be("Go fast");
        migrated.Entries["TESTCODE"].LastSpentAt.Should().BeNull();

        migrated.Entries["ANOTHER"].Effect.Should().Be("Jump High");
        migrated.Entries["ANOTHER"].LastSpentAt.Should().BeNull();
    }

    [Fact]
    public void DiscoveryAndSpent_MergesCorrectly()
    {
        WriteDiscovery("Arthur", "Kwatoxi", new
        {
            schemaVersion = 1,
            discoveries = new Dictionary<string, object>
            {
                ["KNOWNCODE"] = new { code = "KNOWNCODE", effectName = "Anemia", description = "Bad effect", discoveredAt = "2026-01-10T12:00:00+00:00" },
                ["SPENTCODE"] = new { code = "SPENTCODE", effectName = "Run Fast", description = "", discoveredAt = "2026-01-11T14:00:00+00:00" },
            }
        });

        WriteSpent("Arthur", "Kwatoxi", new
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
        migrated.Entries["SPENTCODE"].LastSpentAt!.Value.Year.Should().Be(2026);
    }

    [Fact]
    public void SpentOnly_CreatesEntries_WithUnknownEffect()
    {
        WriteSpent("Arthur", "Kwatoxi", new
        {
            schemaVersion = 1,
            spentAt = new Dictionary<string, string>
            {
                ["ORPHAN"] = "2026-03-01T10:00:00",
            }
        });

        var migration = new SarumanCodebookLegacyMigration(_root);
        var result = migration.TryMigrate("Arthur", "Kwatoxi", out var migrated, out _);

        result.Should().BeTrue();
        migrated.Entries["ORPHAN"].Effect.Should().Be("(unknown)");
        migrated.Entries["ORPHAN"].LastSpentAt.Should().NotBeNull();
    }

    private void WriteDiscovery(string character, string server, object data)
    {
        var dir = Path.Combine(_root, PerCharacterStore<SarumanCodebook>.Slug(character, server));
        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, "wop-discovery.json");
        File.WriteAllText(path, JsonSerializer.Serialize(data, new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            WriteIndented = true,
        }));
    }

    private void WriteSpent(string character, string server, object data)
    {
        var dir = Path.Combine(_root, PerCharacterStore<SarumanCodebook>.Slug(character, server));
        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, "wop-spent.json");
        File.WriteAllText(path, JsonSerializer.Serialize(data, new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            WriteIndented = true,
        }));
    }
}
