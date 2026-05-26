using System.IO;
using System.Text.Json;
using Arda.Composition.Internal;
using FluentAssertions;
using Mithril.Shared.Character;
using Xunit;

namespace Arda.Composition.Tests;

/// <summary>
/// Tests for <see cref="NpcStateArwenMigration"/> which seeds <see cref="NpcStateSnapshot"/>
/// from Arwen's legacy <c>arwen.json</c> favor dictionary on first load.
/// </summary>
public sealed class NpcStateArwenMigrationTests : IDisposable
{
    private readonly string _root;

    public NpcStateArwenMigrationTests()
    {
        _root = Mithril.TestSupport.TestPaths.CreateTempDir("npc-arwen-migration");
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { /* best-effort */ }
    }

    [Fact]
    public void TryMigrate_ReadsArwenFavorEntries()
    {
        var slug = PerCharacterStore<NpcStateSnapshot>.Slug("Arthur", "Kwatoxi");
        var charDir = Path.Combine(_root, slug);
        Directory.CreateDirectory(charDir);

        var arwenJson = """
            {
              "schemaVersion": 1,
              "favor": {
                "NPC_Marna": { "exactFavor": 2847.3, "timestamp": "2026-05-20T10:00:00+00:00" },
                "NPC_Sanja": { "exactFavor": -100.5, "timestamp": "2026-05-19T08:30:00+00:00" }
              }
            }
            """;
        File.WriteAllText(Path.Combine(charDir, "arwen.json"), arwenJson);

        var migration = new NpcStateArwenMigration(_root);
        var result = migration.TryMigrate("Arthur", "Kwatoxi", out var migrated, out var legacyPath);

        result.Should().BeTrue();
        legacyPath.Should().BeEmpty("the migration does not delete arwen.json");

        migrated.Npcs.Should().HaveCount(2);

        migrated.Npcs["NPC_Marna"].AbsoluteFavor.Should().Be(2847.3);
        migrated.Npcs["NPC_Marna"].FavorUpdatedAt.Should().Be(
            new DateTimeOffset(2026, 5, 20, 10, 0, 0, TimeSpan.Zero));

        migrated.Npcs["NPC_Sanja"].AbsoluteFavor.Should().Be(-100.5);
        migrated.Npcs["NPC_Sanja"].FavorUpdatedAt.Should().Be(
            new DateTimeOffset(2026, 5, 19, 8, 30, 0, TimeSpan.Zero));
    }

    [Fact]
    public void TryMigrate_ReturnsFalse_WhenNoArwenFile()
    {
        var slug = PerCharacterStore<NpcStateSnapshot>.Slug("Bob", "Kwatoxi");
        Directory.CreateDirectory(Path.Combine(_root, slug));

        var migration = new NpcStateArwenMigration(_root);
        var result = migration.TryMigrate("Bob", "Kwatoxi", out _, out _);

        result.Should().BeFalse();
    }

    [Fact]
    public void TryMigrate_ReturnsFalse_WhenEmptyFavorDictionary()
    {
        var slug = PerCharacterStore<NpcStateSnapshot>.Slug("Alice", "Kwatoxi");
        var charDir = Path.Combine(_root, slug);
        Directory.CreateDirectory(charDir);

        var arwenJson = """
            {
              "schemaVersion": 1,
              "favor": {}
            }
            """;
        File.WriteAllText(Path.Combine(charDir, "arwen.json"), arwenJson);

        var migration = new NpcStateArwenMigration(_root);
        var result = migration.TryMigrate("Alice", "Kwatoxi", out _, out _);

        result.Should().BeFalse();
    }

    [Fact]
    public void TryMigrate_SkipsEntriesWithoutExactFavor()
    {
        var slug = PerCharacterStore<NpcStateSnapshot>.Slug("Eve", "Kwatoxi");
        var charDir = Path.Combine(_root, slug);
        Directory.CreateDirectory(charDir);

        var arwenJson = """
            {
              "schemaVersion": 1,
              "favor": {
                "NPC_Marna": { "exactFavor": 500.0, "timestamp": "2026-05-20T10:00:00+00:00" },
                "NPC_NoFavor": { "timestamp": "2026-05-20T10:00:00+00:00" }
              }
            }
            """;
        File.WriteAllText(Path.Combine(charDir, "arwen.json"), arwenJson);

        var migration = new NpcStateArwenMigration(_root);
        var result = migration.TryMigrate("Eve", "Kwatoxi", out var migrated, out _);

        result.Should().BeTrue();
        migrated.Npcs.Should().HaveCount(1);
        migrated.Npcs.Should().ContainKey("NPC_Marna");
    }

    [Fact]
    public void TryMigrate_ReturnsFalse_WhenFileIsMalformed()
    {
        var slug = PerCharacterStore<NpcStateSnapshot>.Slug("Mallory", "Kwatoxi");
        var charDir = Path.Combine(_root, slug);
        Directory.CreateDirectory(charDir);

        File.WriteAllText(Path.Combine(charDir, "arwen.json"), "not json at all {{{");

        var migration = new NpcStateArwenMigration(_root);
        var result = migration.TryMigrate("Mallory", "Kwatoxi", out _, out _);

        result.Should().BeFalse();
    }

    [Fact]
    public void TryMigrate_HandlesEntryWithMissingTimestamp()
    {
        var slug = PerCharacterStore<NpcStateSnapshot>.Slug("NoTs", "Kwatoxi");
        var charDir = Path.Combine(_root, slug);
        Directory.CreateDirectory(charDir);

        var arwenJson = """
            {
              "schemaVersion": 1,
              "favor": {
                "NPC_Marna": { "exactFavor": 123.4 }
              }
            }
            """;
        File.WriteAllText(Path.Combine(charDir, "arwen.json"), arwenJson);

        var migration = new NpcStateArwenMigration(_root);
        var result = migration.TryMigrate("NoTs", "Kwatoxi", out var migrated, out _);

        result.Should().BeTrue();
        migrated.Npcs["NPC_Marna"].AbsoluteFavor.Should().Be(123.4);
        migrated.Npcs["NPC_Marna"].FavorUpdatedAt.Should().BeNull();
        migrated.Npcs["NPC_Marna"].LastSeenAt.Should().Be(DateTimeOffset.MinValue);
    }
}
