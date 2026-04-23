using System.IO;
using System.Text.Json;
using FluentAssertions;
using Gorgon.Shared.Character;
using Xunit;

namespace Gorgon.Shared.Tests.Character;

public sealed class PerCharacterStoreTests : IDisposable
{
    private readonly string _root;

    public PerCharacterStoreTests()
    {
        _root = Path.Combine(Path.GetTempPath(), $"gorgon-per-char-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_root);
    }

    public void Dispose()
    {
        TestState.MigrateOverride = null;
        try { Directory.Delete(_root, recursive: true); } catch { }
    }

    [Fact]
    public void Save_then_Load_roundtrips_value_at_expected_path()
    {
        var store = new PerCharacterStore<TestState>(_root, "test.json", TestStateJsonContext.Default.TestState);
        store.Save("Arthur", "Kwatoxi", new TestState { Value = "hello" });

        var expectedPath = Path.Combine(_root, "Arthur_Kwatoxi", "test.json");
        File.Exists(expectedPath).Should().BeTrue();

        var loaded = store.Load("Arthur", "Kwatoxi");
        loaded.Value.Should().Be("hello");
    }

    [Fact]
    public void Slug_sanitizes_invalid_filename_chars()
    {
        PerCharacterStore<TestState>.Slug("Arthur/Foo", "Kwat:oxi")
            .Should().Be("Arthur_Foo_Kwat_oxi");
    }

    [Fact]
    public void Save_stamps_CurrentVersion_even_when_caller_supplied_older()
    {
        var store = new PerCharacterStore<TestState>(_root, "test.json", TestStateJsonContext.Default.TestState);
        var state = new TestState { SchemaVersion = 0, Value = "x" };
        store.Save("A", "S", state);

        state.SchemaVersion.Should().Be(TestState.CurrentVersion);

        var path = store.GetFilePath("A", "S");
        var reparsed = JsonSerializer.Deserialize(File.ReadAllText(path), TestStateJsonContext.Default.TestState)!;
        reparsed.SchemaVersion.Should().Be(TestState.CurrentVersion);
    }

    [Fact]
    public void Load_routes_older_version_through_Migrate_hook()
    {
        var store = new PerCharacterStore<TestState>(_root, "test.json", TestStateJsonContext.Default.TestState);
        var path = store.GetFilePath("A", "S");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, """{"schemaVersion":1,"value":"old"}""");

        var sawOldVersion = 0;
        TestState.MigrateOverride = loaded =>
        {
            sawOldVersion = loaded.SchemaVersion;
            return new TestState { Value = loaded.Value + "_migrated" };
        };

        var loaded = store.Load("A", "S");

        sawOldVersion.Should().Be(1);
        loaded.Value.Should().Be("old_migrated");
        loaded.SchemaVersion.Should().Be(TestState.CurrentVersion);
    }

    [Fact]
    public void Load_does_not_invoke_Migrate_when_SchemaVersion_matches_current()
    {
        var store = new PerCharacterStore<TestState>(_root, "test.json", TestStateJsonContext.Default.TestState);
        store.Save("A", "S", new TestState { Value = "current" });

        var called = false;
        TestState.MigrateOverride = _ => { called = true; return new TestState(); };

        var loaded = store.Load("A", "S");

        called.Should().BeFalse();
        loaded.Value.Should().Be("current");
    }

    [Fact]
    public void Load_runs_legacy_migration_when_target_missing_then_deletes_legacy()
    {
        var legacyDir = Path.Combine(_root, "legacy");
        Directory.CreateDirectory(legacyDir);
        var legacyPath = Path.Combine(legacyDir, "old.json");
        File.WriteAllText(legacyPath, "unused — the fake migration fabricates the value");

        var migration = new FakeLegacyMigration { ProducedValue = "from-legacy", LegacyPath = legacyPath };
        var store = new PerCharacterStore<TestState>(_root, "test.json", TestStateJsonContext.Default.TestState, migration);

        var loaded = store.Load("A", "S");

        loaded.Value.Should().Be("from-legacy");
        loaded.SchemaVersion.Should().Be(TestState.CurrentVersion);

        File.Exists(store.GetFilePath("A", "S")).Should().BeTrue("new-path file should be written");
        File.Exists(legacyPath).Should().BeFalse("legacy file should be deleted");
        Directory.Exists(legacyDir).Should().BeFalse("empty legacy parent dir should be deleted");
    }

    [Fact]
    public void Load_does_not_run_legacy_migration_when_target_exists()
    {
        var store = new PerCharacterStore<TestState>(_root, "test.json", TestStateJsonContext.Default.TestState,
            new FakeLegacyMigration { ProducedValue = "LEGACY-WINS", LegacyPath = Path.Combine(_root, "legacy-ignored.json") });
        store.Save("A", "S", new TestState { Value = "current-wins" });

        var loaded = store.Load("A", "S");

        loaded.Value.Should().Be("current-wins");
    }

    [Fact]
    public void Load_returns_fresh_state_at_current_version_when_neither_target_nor_legacy_exist()
    {
        var store = new PerCharacterStore<TestState>(_root, "test.json", TestStateJsonContext.Default.TestState);

        var loaded = store.Load("A", "S");

        loaded.Value.Should().Be("");
        loaded.SchemaVersion.Should().Be(TestState.CurrentVersion);
    }

    [Fact]
    public void Slug_requires_both_character_and_server()
    {
        var act1 = () => PerCharacterStore<TestState>.Slug("", "S");
        var act2 = () => PerCharacterStore<TestState>.Slug("A", "");
        act1.Should().Throw<ArgumentException>();
        act2.Should().Throw<ArgumentException>();
    }

    private sealed class FakeLegacyMigration : ILegacyMigration<TestState>
    {
        public string ProducedValue { get; set; } = "";
        public string LegacyPath { get; set; } = "";

        public bool TryMigrate(string character, string server, out TestState migrated, out string legacyPath)
        {
            legacyPath = LegacyPath;
            if (string.IsNullOrEmpty(LegacyPath) || !File.Exists(LegacyPath))
            {
                migrated = new TestState();
                return false;
            }
            migrated = new TestState { Value = ProducedValue };
            return true;
        }
    }
}
