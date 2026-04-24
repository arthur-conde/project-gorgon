using System.IO;
using System.Text.Json;
using FluentAssertions;
using Gandalf.Domain;
using Gandalf.Services;
using Gorgon.Shared.Character;
using Gorgon.Shared.Settings;
using Xunit;

namespace Gandalf.Tests;

public class GandalfSplitMigrationTests : IDisposable
{
    private readonly string _dir;
    private readonly string _charactersDir;
    private readonly string _defsPath;

    public GandalfSplitMigrationTests()
    {
        _dir = Gorgon.TestSupport.TestPaths.CreateTempDir("gandalf_split");
        _charactersDir = Path.Combine(_dir, "characters");
        Directory.CreateDirectory(_charactersDir);
        _defsPath = Path.Combine(_dir, "definitions.json");
    }

    public void Dispose()
    {
        // Defender / Search indexer occasionally hold a transient handle on freshly closed
        // files in %TEMP%, which makes recursive delete throw IOException or
        // DirectoryNotFoundException under parallel-test load. Cleanup is best-effort —
        // the OS reclaims the temp dir on its own schedule, and a leak here doesn't fail the test.
        try { Directory.Delete(_dir, recursive: true); } catch { /* best-effort */ }
    }

    private string SeedV1Blob(string character, string server, string json)
    {
        var charDir = Path.Combine(_charactersDir, $"{character}_{server}");
        Directory.CreateDirectory(charDir);
        var path = Path.Combine(charDir, "gandalf.json");
        File.WriteAllText(path, json);
        return path;
    }

    private async Task RunMigrationAsync()
    {
        var defStore = new JsonSettingsStore<GandalfDefinitions>(_defsPath,
            GandalfDefinitionsJsonContext.Default.GandalfDefinitions);
        var progressStore = new PerCharacterStore<GandalfProgress>(_charactersDir, "gandalf.json",
            GandalfProgressJsonContext.Default.GandalfProgress);
        var active = new FakeActiveCharacterService();
        var view = new PerCharacterView<GandalfProgress>(active, progressStore);

        var migration = new GandalfSplitMigration(
            new PerCharacterStoreOptions { CharactersRootDir = _charactersDir },
            defStore, progressStore, view);

        await migration.StartAsync(CancellationToken.None);
        view.Dispose();
    }

    private static string V1Blob(params (string Id, string Name, int Hours, DateTimeOffset? StartedAt)[] timers)
    {
        var sb = new System.Text.StringBuilder();
        sb.Append("""{"schemaVersion":1,"timers":[""");
        for (var i = 0; i < timers.Length; i++)
        {
            if (i > 0) sb.Append(',');
            var t = timers[i];
            sb.Append('{');
            sb.Append($"\"id\":\"{t.Id}\",");
            sb.Append($"\"name\":\"{t.Name}\",");
            sb.Append($"\"duration\":\"{t.Hours:00}:00:00\",");
            sb.Append("\"region\":\"Serbule\",\"map\":\"\",");
            sb.Append($"\"startedAt\":{(t.StartedAt is { } s ? $"\"{s:O}\"" : "null")},");
            sb.Append("\"completedAt\":null");
            sb.Append('}');
        }
        sb.Append("]}");
        return sb.ToString();
    }

    [Fact]
    public async Task Two_characters_with_overlapping_timers_union_into_global_definitions()
    {
        var started = DateTimeOffset.UtcNow - TimeSpan.FromMinutes(10);
        SeedV1Blob("Arthur", "Kwatoxi", V1Blob(
            ("shared", "Chest", 1, started),
            ("arthur-only", "Arthur's Crypt", 2, null)));
        SeedV1Blob("Bilbo", "Kwatoxi", V1Blob(
            ("shared", "Chest", 1, null),
            ("bilbo-only", "Bilbo's Barrow", 3, null)));

        await RunMigrationAsync();

        // Global definitions has all three unique ids.
        var defs = JsonSerializer.Deserialize(File.ReadAllText(_defsPath),
            GandalfDefinitionsJsonContext.Default.GandalfDefinitions);
        defs.Should().NotBeNull();
        defs!.Timers.Select(t => t.Id).Should().BeEquivalentTo("shared", "arthur-only", "bilbo-only");

        // Arthur's per-char file is v2-shaped with one progress entry (shared is running).
        var arthurProgress = JsonSerializer.Deserialize(
            File.ReadAllText(Path.Combine(_charactersDir, "Arthur_Kwatoxi", "gandalf.json")),
            GandalfProgressJsonContext.Default.GandalfProgress);
        arthurProgress.Should().NotBeNull();
        arthurProgress!.SchemaVersion.Should().Be(GandalfProgress.Version);
        arthurProgress.ByTimerId.Should().HaveCount(1);
        arthurProgress.ByTimerId.Should().ContainKey("shared");
        arthurProgress.ByTimerId["shared"].StartedAt.Should().NotBeNull();

        // Bilbo's per-char file has no progress entries — nothing was started.
        var bilboProgress = JsonSerializer.Deserialize(
            File.ReadAllText(Path.Combine(_charactersDir, "Bilbo_Kwatoxi", "gandalf.json")),
            GandalfProgressJsonContext.Default.GandalfProgress);
        bilboProgress.Should().NotBeNull();
        bilboProgress!.SchemaVersion.Should().Be(GandalfProgress.Version);
        bilboProgress.ByTimerId.Should().BeEmpty();
    }

    [Fact]
    public async Task Rerun_is_a_noop_when_already_migrated()
    {
        SeedV1Blob("Arthur", "Kwatoxi", V1Blob(("a", "A", 1, null)));
        await RunMigrationAsync();

        var defsTime1 = File.GetLastWriteTimeUtc(_defsPath);
        var progressPath = Path.Combine(_charactersDir, "Arthur_Kwatoxi", "gandalf.json");
        var progressTime1 = File.GetLastWriteTimeUtc(progressPath);

        await Task.Delay(50);
        await RunMigrationAsync();

        File.GetLastWriteTimeUtc(_defsPath).Should().Be(defsTime1, "definitions file untouched on second run");
        File.GetLastWriteTimeUtc(progressPath).Should().Be(progressTime1, "per-char file untouched on second run");
    }

    [Fact]
    public async Task Resume_mid_migration_completes_missing_per_char_rewrite()
    {
        // Simulate crash: defs landed, Arthur's v1 blob still present.
        var defStore = new JsonSettingsStore<GandalfDefinitions>(_defsPath,
            GandalfDefinitionsJsonContext.Default.GandalfDefinitions);
        defStore.Save(new GandalfDefinitions
        {
            Timers = [new GandalfTimerDef { Id = "abc", Name = "Resumed", Duration = TimeSpan.FromHours(1) }],
        });
        SeedV1Blob("Arthur", "Kwatoxi", V1Blob(("abc", "Resumed", 1, null)));

        await RunMigrationAsync();

        var arthur = JsonSerializer.Deserialize(
            File.ReadAllText(Path.Combine(_charactersDir, "Arthur_Kwatoxi", "gandalf.json")),
            GandalfProgressJsonContext.Default.GandalfProgress);
        arthur.Should().NotBeNull();
        arthur!.SchemaVersion.Should().Be(GandalfProgress.Version);
    }

    [Fact]
    public async Task Empty_v1_blob_becomes_empty_v2_progress()
    {
        SeedV1Blob("Arthur", "Kwatoxi", """{"schemaVersion":1,"timers":[]}""");

        await RunMigrationAsync();

        var progress = JsonSerializer.Deserialize(
            File.ReadAllText(Path.Combine(_charactersDir, "Arthur_Kwatoxi", "gandalf.json")),
            GandalfProgressJsonContext.Default.GandalfProgress);
        progress.Should().NotBeNull();
        progress!.ByTimerId.Should().BeEmpty();
        progress.SchemaVersion.Should().Be(GandalfProgress.Version);
    }
}
