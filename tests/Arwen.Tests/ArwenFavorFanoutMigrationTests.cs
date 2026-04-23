using System.IO;
using System.Text.Json;
using Arwen.Domain;
using Arwen.State;
using FluentAssertions;
using Gorgon.Shared.Character;
using Gorgon.Shared.Settings;
using Gorgon.Shared.Storage;
using Xunit;

namespace Arwen.Tests;

public sealed class ArwenFavorFanoutMigrationTests : IDisposable
{
    private readonly string _root;
    private readonly string _arwenDir;
    private readonly string _charactersRoot;

    public ArwenFavorFanoutMigrationTests()
    {
        _root = Path.Combine(Path.GetTempPath(), $"gorgon-arwen-fanout-{Guid.NewGuid():N}");
        _arwenDir = Path.Combine(_root, "Arwen");
        _charactersRoot = Path.Combine(_root, "characters");
        Directory.CreateDirectory(_arwenDir);
        Directory.CreateDirectory(_charactersRoot);
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { }
    }

    [Fact]
    public async Task Splits_legacy_FavorStates_into_per_character_files_and_trims_settings()
    {
        // Seed legacy settings.json with FavorStates for two characters.
        var settingsPath = Path.Combine(_arwenDir, "settings.json");
        var legacy = """
        {
          "favorStates": {
            "Arthur": { "Therese": { "exactFavor": 1234.5, "timestamp": "2026-04-01T00:00:00+00:00" } },
            "Bilbo":  { "Yetta":   { "exactFavor":  500.0, "timestamp": "2026-04-02T00:00:00+00:00" } }
          },
          "calibration": { "mergeMode": "Blend" }
        }
        """;
        File.WriteAllText(settingsPath, legacy);

        var settingsStore = new JsonSettingsStore<ArwenSettings>(settingsPath, ArwenJsonContext.Default.ArwenSettings);
        var settings = settingsStore.Load();

        var active = new FakeActiveCharacterService
        {
            Characters =
            [
                new CharacterSnapshot("Arthur", "Kwatoxi", default,
                    new Dictionary<string, CharacterSkill>(), new Dictionary<string, int>(),
                    new Dictionary<string, string>()),
                new CharacterSnapshot("Bilbo", "Kwatoxi", default,
                    new Dictionary<string, CharacterSkill>(), new Dictionary<string, int>(),
                    new Dictionary<string, string>()),
            ],
        };

        var store = new PerCharacterStore<ArwenFavorState>(_charactersRoot, "arwen.json",
            ArwenFavorStateJsonContext.Default.ArwenFavorState);

        var migration = new ArwenFavorFanoutMigration(_arwenDir, store, active, settingsStore, settings);
        await migration.StartAsync(CancellationToken.None);

        // Per-character files exist with the right content.
        var arthurFavor = store.Load("Arthur", "Kwatoxi");
        arthurFavor.Favor.Should().ContainKey("Therese");
        arthurFavor.Favor["Therese"].ExactFavor.Should().Be(1234.5);

        var bilboFavor = store.Load("Bilbo", "Kwatoxi");
        bilboFavor.Favor.Should().ContainKey("Yetta");
        bilboFavor.Favor["Yetta"].ExactFavor.Should().Be(500.0);

        // Settings file was rewritten without favorStates.
        var rewritten = File.ReadAllText(settingsPath);
        rewritten.Should().NotContain("favorStates");
    }

    [Fact]
    public async Task Retains_legacy_file_when_any_character_is_unresolved()
    {
        var settingsPath = Path.Combine(_arwenDir, "settings.json");
        File.WriteAllText(settingsPath, """
        {
          "favorStates": {
            "Arthur": { "Therese": { "exactFavor": 1234.5, "timestamp": "2026-04-01T00:00:00+00:00" } },
            "Stranger": { "Yetta": { "exactFavor": 500.0, "timestamp": "2026-04-02T00:00:00+00:00" } }
          }
        }
        """);

        var settingsStore = new JsonSettingsStore<ArwenSettings>(settingsPath, ArwenJsonContext.Default.ArwenSettings);
        var settings = settingsStore.Load();

        // Only Arthur has an export; Stranger is unknown.
        var active = new FakeActiveCharacterService
        {
            Characters =
            [
                new CharacterSnapshot("Arthur", "Kwatoxi", default,
                    new Dictionary<string, CharacterSkill>(), new Dictionary<string, int>(),
                    new Dictionary<string, string>()),
            ],
        };

        var store = new PerCharacterStore<ArwenFavorState>(_charactersRoot, "arwen.json",
            ArwenFavorStateJsonContext.Default.ArwenFavorState);

        var migration = new ArwenFavorFanoutMigration(_arwenDir, store, active, settingsStore, settings);
        await migration.StartAsync(CancellationToken.None);

        // Arthur migrated.
        store.Load("Arthur", "Kwatoxi").Favor.Should().ContainKey("Therese");

        // Legacy FavorStates remains in place for retry on next startup.
        var text = File.ReadAllText(settingsPath);
        text.Should().Contain("favorStates");
        text.Should().Contain("Stranger");
    }

    [Fact]
    public async Task NoOp_when_legacy_file_missing()
    {
        var settingsPath = Path.Combine(_arwenDir, "settings.json");
        var settingsStore = new JsonSettingsStore<ArwenSettings>(settingsPath, ArwenJsonContext.Default.ArwenSettings);
        var settings = new ArwenSettings();
        var active = new FakeActiveCharacterService();
        var store = new PerCharacterStore<ArwenFavorState>(_charactersRoot, "arwen.json",
            ArwenFavorStateJsonContext.Default.ArwenFavorState);

        var migration = new ArwenFavorFanoutMigration(_arwenDir, store, active, settingsStore, settings);
        var act = async () => await migration.StartAsync(CancellationToken.None);
        await act.Should().NotThrowAsync();
    }
}
