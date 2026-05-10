using System.IO;
using System.Text.Json;
using FluentAssertions;
using Mithril.Shared.Character;
using Mithril.Shared.Reference;
using Pippin.Domain;
using Pippin.State;
using Xunit;

namespace Pippin.Tests;

[Trait("Category", "FileIO")]
[Collection("FileIO")]
public sealed class GourmandStateServiceMigrationTests : IDisposable
{
    private readonly string _root;
    private readonly string _legacyDir;
    private readonly string _charactersRoot;

    public GourmandStateServiceMigrationTests()
    {
        _root = Mithril.TestSupport.TestPaths.CreateTempDir("mithril-pippin-mig");
        _legacyDir = Path.Combine(_root, "Mithril", "Pippin");
        _charactersRoot = Path.Combine(_root, "Mithril", "characters");
        Directory.CreateDirectory(_legacyDir);
        Directory.CreateDirectory(_charactersRoot);
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { }
    }

    [Fact]
    public void First_load_migrates_legacy_flat_file_into_active_character_directory_with_pending_data()
    {
        // Seed the pre-per-character flat file, written in the v1 shape (display-name keys).
        var legacyFile = Path.Combine(_legacyDir, "gourmand-state.json");
        File.WriteAllText(legacyFile, """
            {
                "schemaVersion": 1,
                "eatenFoods": { "Bacon": 3, "Apple Juice": 7 },
                "lastReportTime": "2026-04-01T12:00:00+00:00"
            }
            """);

        var migration = new GourmandLegacyMigration(_legacyDir, GourmandStateJsonContext.Default.GourmandState);
        var store = new PerCharacterStore<GourmandState>(
            _charactersRoot, "pippin.json",
            GourmandStateJsonContext.Default.GourmandState,
            migration);

        var loaded = store.Load("Arthur", "Kwatoxi");

        // After Phase 1 (no catalog yet), the legacy display-name dict is parked for later
        // resolution rather than being placed into the InternalName dict.
        loaded.SchemaVersion.Should().Be(GourmandState.CurrentVersion);
        loaded.EatenFoodsByInternalName.Should().BeEmpty();
        loaded.UnknownByName.Should().BeEmpty();
        loaded.PendingLegacyByName.Should().NotBeNull();
        loaded.PendingLegacyByName!.Should().ContainKey("Bacon").WhoseValue.Should().Be(3);
        loaded.PendingLegacyByName.Should().ContainKey("Apple Juice").WhoseValue.Should().Be(7);

        File.Exists(Path.Combine(_charactersRoot, "Arthur_Kwatoxi", "pippin.json"))
            .Should().BeTrue("the per-character file should exist after migration");
        File.Exists(legacyFile).Should().BeFalse("legacy flat file should be deleted");
        Directory.Exists(_legacyDir).Should().BeFalse("empty legacy Pippin dir should be cleaned up");
    }

    [Fact]
    public void Phase2_promote_resolves_legacy_through_catalog_and_clears_pending()
    {
        var legacyFile = Path.Combine(_legacyDir, "gourmand-state.json");
        File.WriteAllText(legacyFile, """
            {
                "schemaVersion": 1,
                "eatenFoods": { "Bacon": 3, "Apple Juice": 7, "Mystery Stew": 1 }
            }
            """);

        var migration = new GourmandLegacyMigration(_legacyDir, GourmandStateJsonContext.Default.GourmandState);
        var store = new PerCharacterStore<GourmandState>(
            _charactersRoot, "pippin.json",
            GourmandStateJsonContext.Default.GourmandState,
            migration);

        var loaded = store.Load("Arthur", "Kwatoxi");
        loaded.PendingLegacyByName.Should().NotBeNull();

        // Now simulate the catalog being available: drain the pending dict through it
        // exactly the way GourmandStateService does. Bacon + Apple Juice resolve;
        // Mystery Stew falls through to UnknownByName.
        var refData = new StubRefData(new Dictionary<long, ItemEntry>
        {
            [1] = new(1, "Apple Juice", "FoodAppleJuice", 1, 0, [], FoodDesc: "Level 0 Snack"),
            [2] = new(2, "Bacon", "FoodBacon", 1, 0, [], FoodDesc: "Level 0 Snack"),
        });
        var catalog = new FoodCatalog(refData);
        var sm = new GourmandStateMachine(catalog);
        sm.Hydrate(loaded);
        sm.ApplyLegacyByName(loaded.PendingLegacyByName!);

        sm.EatenFoodsByInternalName.Should().HaveCount(2);
        sm.EatenFoodsByInternalName["FoodAppleJuice"].Should().Be(7);
        sm.EatenFoodsByInternalName["FoodBacon"].Should().Be(3);
        sm.UnknownByName.Should().ContainKey("Mystery Stew").WhoseValue.Should().Be(1);
    }

    [Fact]
    public void Second_character_after_migration_starts_with_fresh_state()
    {
        var legacyFile = Path.Combine(_legacyDir, "gourmand-state.json");
        File.WriteAllText(legacyFile, """
            { "schemaVersion": 1, "eatenFoods": { "Bacon": 3 } }
            """);

        var migration = new GourmandLegacyMigration(_legacyDir, GourmandStateJsonContext.Default.GourmandState);
        var store = new PerCharacterStore<GourmandState>(
            _charactersRoot, "pippin.json",
            GourmandStateJsonContext.Default.GourmandState,
            migration);

        // First character consumes the legacy data.
        _ = store.Load("Arthur", "Kwatoxi");
        // Second character: legacy already consumed + deleted, so this is a clean slate.
        var second = store.Load("Bilbo", "Kwatoxi");

        second.EatenFoodsByInternalName.Should().BeEmpty();
        second.UnknownByName.Should().BeEmpty();
        second.PendingLegacyByName.Should().BeNull();
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

        loaded.EatenFoodsByInternalName.Should().BeEmpty();
        loaded.UnknownByName.Should().BeEmpty();
        loaded.PendingLegacyByName.Should().BeNull();
        loaded.SchemaVersion.Should().Be(GourmandState.CurrentVersion);
    }

    [Fact]
    public void V2_file_round_trips_without_back_compat_property_in_output()
    {
        var path = Path.Combine(_charactersRoot, "Arthur_Kwatoxi", "pippin.json");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);

        // Write a v2 file by serializing the current shape.
        var state = new GourmandState
        {
            EatenFoodsByInternalName = new Dictionary<string, int>(StringComparer.Ordinal) { ["FoodBacon"] = 3 },
            UnknownByName = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase) { ["Mystery Stew"] = 1 },
            LastReportTime = DateTimeOffset.UtcNow,
        };
        File.WriteAllText(path, JsonSerializer.Serialize(state, GourmandStateJsonContext.Default.GourmandState));

        File.ReadAllText(path).Should().NotContain("\"eatenFoods\"",
            "v2 output must not write the back-compat eatenFoods key");

        var store = new PerCharacterStore<GourmandState>(
            _charactersRoot, "pippin.json",
            GourmandStateJsonContext.Default.GourmandState,
            null);
        var loaded = store.Load("Arthur", "Kwatoxi");

        loaded.SchemaVersion.Should().Be(GourmandState.CurrentVersion);
        loaded.EatenFoodsByInternalName.Should().ContainKey("FoodBacon").WhoseValue.Should().Be(3);
        loaded.UnknownByName.Should().ContainKey("Mystery Stew").WhoseValue.Should().Be(1);
        loaded.PendingLegacyByName.Should().BeNull();
    }

    private sealed class StubRefData : IReferenceDataService
    {
        public StubRefData(Dictionary<long, ItemEntry> items) { Items = items; }

        public IReadOnlyList<string> Keys { get; } = [];
        public IReadOnlyDictionary<long, ItemEntry> Items { get; }
        public IReadOnlyDictionary<string, ItemEntry> ItemsByInternalName { get; } = new Dictionary<string, ItemEntry>();
        public ItemKeywordIndex KeywordIndex => ItemKeywordIndex.Empty;
        public IReadOnlyDictionary<string, RecipeEntry> Recipes { get; } = new Dictionary<string, RecipeEntry>();
        public IReadOnlyDictionary<string, RecipeEntry> RecipesByInternalName { get; } = new Dictionary<string, RecipeEntry>();
        public IReadOnlyDictionary<string, SkillEntry> Skills { get; } = new Dictionary<string, SkillEntry>();
        public IReadOnlyDictionary<string, XpTableEntry> XpTables { get; } = new Dictionary<string, XpTableEntry>();
        public IReadOnlyDictionary<string, NpcEntry> Npcs { get; } = new Dictionary<string, NpcEntry>();
        public IReadOnlyDictionary<string, AreaEntry> Areas { get; } = new Dictionary<string, AreaEntry>(StringComparer.Ordinal);
        public IReadOnlyDictionary<string, IReadOnlyList<ItemSource>> ItemSources { get; } = new Dictionary<string, IReadOnlyList<ItemSource>>();
        public IReadOnlyDictionary<string, AttributeEntry> Attributes { get; } = new Dictionary<string, AttributeEntry>();
        public IReadOnlyDictionary<string, PowerEntry> Powers { get; } = new Dictionary<string, PowerEntry>();
        public IReadOnlyDictionary<string, IReadOnlyList<string>> Profiles { get; } = new Dictionary<string, IReadOnlyList<string>>();
        public IReadOnlyDictionary<string, QuestEntry> Quests { get; } = new Dictionary<string, QuestEntry>();
        public IReadOnlyDictionary<string, QuestEntry> QuestsByInternalName { get; } = new Dictionary<string, QuestEntry>();
        public IReadOnlyDictionary<string, string> Strings { get; } = new Dictionary<string, string>(StringComparer.Ordinal);
        public event EventHandler<string>? FileUpdated;
        public ReferenceFileSnapshot GetSnapshot(string key) => new(key, ReferenceFileSource.Bundled, "", null, 0);
        public Task RefreshAsync(string key, CancellationToken ct = default) => Task.CompletedTask;
        public Task RefreshAllAsync(CancellationToken ct = default) => Task.CompletedTask;
        public void BeginBackgroundRefresh() { }
        private void Suppress() => FileUpdated?.Invoke(this, "");
    }
}
