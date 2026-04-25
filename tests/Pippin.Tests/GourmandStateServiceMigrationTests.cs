using System.IO;
using System.Text.Json;
using FluentAssertions;
using Gorgon.Shared.Character;
using Pippin.Domain;
using Pippin.State;
using Xunit;

namespace Pippin.Tests;

[Trait("Category", "FileIO")]
public sealed class GourmandStateServiceMigrationTests : IDisposable
{
    private readonly string _root;
    private readonly string _legacyDir;
    private readonly string _charactersRoot;

    public GourmandStateServiceMigrationTests()
    {
        _root = Gorgon.TestSupport.TestPaths.CreateTempDir("gorgon-pippin-mig");
        _legacyDir = Path.Combine(_root, "Gorgon", "Pippin");
        _charactersRoot = Path.Combine(_root, "Gorgon", "characters");
        Directory.CreateDirectory(_legacyDir);
        Directory.CreateDirectory(_charactersRoot);
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { }
    }

    [Fact]
    public void First_load_migrates_legacy_flat_file_into_active_character_directory()
    {
        // Seed the pre-per-character flat file.
        var legacyFile = Path.Combine(_legacyDir, "gourmand-state.json");
        File.WriteAllText(legacyFile, JsonSerializer.Serialize(
            new GourmandState
            {
                EatenFoods = new Dictionary<string, int> { ["Bacon"] = 3, ["Apple Juice"] = 7 },
                LastReportTime = DateTimeOffset.UtcNow,
            },
            GourmandStateJsonContext.Default.GourmandState));

        var migration = new GourmandLegacyMigration(_legacyDir, GourmandStateJsonContext.Default.GourmandState);
        var store = new PerCharacterStore<GourmandState>(
            _charactersRoot, "pippin.json",
            GourmandStateJsonContext.Default.GourmandState,
            migration);

        var loaded = store.Load("Arthur", "Kwatoxi");

        loaded.EatenFoods.Should().ContainKey("Bacon").WhoseValue.Should().Be(3);
        loaded.EatenFoods.Should().ContainKey("Apple Juice").WhoseValue.Should().Be(7);
        loaded.SchemaVersion.Should().Be(GourmandState.CurrentVersion);

        File.Exists(Path.Combine(_charactersRoot, "Arthur_Kwatoxi", "pippin.json"))
            .Should().BeTrue("the per-character file should exist after migration");
        File.Exists(legacyFile).Should().BeFalse("legacy flat file should be deleted");
        Directory.Exists(_legacyDir).Should().BeFalse("empty legacy Pippin dir should be cleaned up");
    }

    [Fact]
    public void Second_character_after_migration_starts_with_fresh_state()
    {
        var legacyFile = Path.Combine(_legacyDir, "gourmand-state.json");
        File.WriteAllText(legacyFile, JsonSerializer.Serialize(
            new GourmandState { EatenFoods = new Dictionary<string, int> { ["Bacon"] = 3 } },
            GourmandStateJsonContext.Default.GourmandState));

        var migration = new GourmandLegacyMigration(_legacyDir, GourmandStateJsonContext.Default.GourmandState);
        var store = new PerCharacterStore<GourmandState>(
            _charactersRoot, "pippin.json",
            GourmandStateJsonContext.Default.GourmandState,
            migration);

        // First character consumes the legacy data.
        _ = store.Load("Arthur", "Kwatoxi");
        // Second character: legacy already consumed + deleted, so this is a clean slate.
        var second = store.Load("Bilbo", "Kwatoxi");

        second.EatenFoods.Should().BeEmpty();
        second.SchemaVersion.Should().Be(GourmandState.CurrentVersion);
    }

    [Fact]
    public void When_no_legacy_file_first_load_returns_fresh_state()
    {
        var migration = new GourmandLegacyMigration(_legacyDir, GourmandStateJsonContext.Default.GourmandState);
        var store = new PerCharacterStore<GourmandState>(
            _charactersRoot, "pippin.json",
            GourmandStateJsonContext.Default.GourmandState,
            migration);

        var loaded = store.Load("Arthur", "Kwatoxi");

        loaded.EatenFoods.Should().BeEmpty();
        loaded.SchemaVersion.Should().Be(GourmandState.CurrentVersion);
    }
}
