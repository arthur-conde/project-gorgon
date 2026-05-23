using System.IO;
using System.Text.Json;
using FluentAssertions;
using Mithril.Shared.Character;
using Saruman.Services;
using Saruman.Settings;
using Xunit;

namespace Saruman.Tests.Settings;

/// <summary>
/// Migration-hint contract for #686: the schema 1→2 upgrade and the legacy
/// flat-file migration both drop the pre-#603 spent-flag state, so both paths
/// surface <see cref="SarumanState.ShowPreSplitMigrationHint"/> for the UI to
/// expose the one-time recovery banner. Fresh installs never flip the flag.
/// </summary>
[Trait("Category", "FileIO")]
[Collection("FileIO")]
public sealed class SarumanStateMigrationTests : IDisposable
{
    private readonly string _root;

    public SarumanStateMigrationTests()
    {
        _root = Mithril.TestSupport.TestPaths.CreateTempDir("saruman-migration-hint");
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { }
    }

    [Fact]
    public void Migrate_from_v1_sets_ShowPreSplitMigrationHint()
    {
        var v1 = new SarumanState { SchemaVersion = 1 };
        var migrated = SarumanState.Migrate(v1);
        migrated.SchemaVersion.Should().Be(SarumanState.Version);
        migrated.ShowPreSplitMigrationHint.Should().BeTrue();
        migrated.SpentOverrides.Should().BeEmpty();
    }

    [Fact]
    public void Fresh_state_does_not_show_migration_hint()
    {
        // Default state (no migration ever runs): the flag stays false.
        new SarumanState().ShowPreSplitMigrationHint.Should().BeFalse();
    }

    [Fact]
    public void PerCharacterStore_v1_file_on_disk_surfaces_hint_then_dismiss_persists()
    {
        // Simulates the live-upgrade path: a user's existing saruman.json
        // was saved at v1, the new Mithril build loads it, Migrate runs, the
        // hint flag is stamped. After dismiss + save the flag stays false
        // across restarts (Migrate doesn't re-run for current-version files).
        var charDir = Path.Combine(_root, "Arthur_Kwatoxi");
        Directory.CreateDirectory(charDir);
        var path = Path.Combine(charDir, "saruman.json");
        File.WriteAllText(path, """{"schemaVersion":1,"spentOverrides":[]}""");

        var store = new PerCharacterStore<SarumanState>(_root, "saruman.json",
            SarumanJsonContext.Default.SarumanState);

        var loaded = store.Load("Arthur", "Kwatoxi");
        loaded.SchemaVersion.Should().Be(SarumanState.Version);
        loaded.ShowPreSplitMigrationHint.Should().BeTrue();

        // User dismisses, we save.
        loaded.ShowPreSplitMigrationHint = false;
        store.Save("Arthur", "Kwatoxi", loaded);

        var reloaded = store.Load("Arthur", "Kwatoxi");
        reloaded.SchemaVersion.Should().Be(SarumanState.Version);
        reloaded.ShowPreSplitMigrationHint.Should().BeFalse(
            "Migrate only runs for older-than-current state, so a dismissed-and-saved file stays dismissed");
    }

    [Fact]
    public void Legacy_migration_surfaces_hint_when_pre603_flat_file_is_imported()
    {
        // Pre-#603 flat-file at %LocalAppData%/Mithril/Saruman/settings.json
        // carried the old Codebook field; STJ silently drops it during
        // deserialization (the type no longer has the field). The legacy
        // migration is the import boundary — it must set the hint flag too,
        // since the schema 1→2 Migrate() never runs on this path (the
        // PerCharacterStore forces SchemaVersion = CurrentVersion directly).
        var legacyDir = Path.Combine(_root, "_legacy_saruman");
        Directory.CreateDirectory(legacyDir);
        File.WriteAllText(Path.Combine(legacyDir, "settings.json"),
            """{"schemaVersion":1,"codebook":{},"spentOverrides":["LEGACY"]}""");

        var legacy = new SarumanLegacyMigration(legacyDir, SarumanJsonContext.Default.SarumanState);
        legacy.TryMigrate("Arthur", "Kwatoxi", out var migrated, out _).Should().BeTrue();
        migrated.ShowPreSplitMigrationHint.Should().BeTrue();
        // SpentOverrides that survived deserialization (post-#603 field) carry over.
        migrated.SpentOverrides.Should().Contain("LEGACY");
    }

    [Fact]
    public void PerCharacterStore_with_legacy_fallback_round_trips_hint_dismissal()
    {
        // End-to-end legacy-path round-trip: only the legacy flat file exists,
        // PerCharacterStore picks it up via the registered ILegacyMigration,
        // writes the new per-character file with the hint set, and a later
        // dismiss + save keeps the hint off on subsequent loads.
        var legacyDir = Path.Combine(_root, "_legacy_saruman");
        Directory.CreateDirectory(legacyDir);
        File.WriteAllText(Path.Combine(legacyDir, "settings.json"),
            """{"schemaVersion":1,"spentOverrides":[]}""");

        var legacy = new SarumanLegacyMigration(legacyDir, SarumanJsonContext.Default.SarumanState);
        var store = new PerCharacterStore<SarumanState>(_root, "saruman.json",
            SarumanJsonContext.Default.SarumanState, legacy);

        var loaded = store.Load("Arthur", "Kwatoxi");
        loaded.ShowPreSplitMigrationHint.Should().BeTrue();
        // Legacy file is cleaned up by the store on successful import.
        File.Exists(Path.Combine(legacyDir, "settings.json")).Should().BeFalse();

        loaded.ShowPreSplitMigrationHint = false;
        store.Save("Arthur", "Kwatoxi", loaded);

        var reloaded = store.Load("Arthur", "Kwatoxi");
        reloaded.ShowPreSplitMigrationHint.Should().BeFalse();
    }
}
