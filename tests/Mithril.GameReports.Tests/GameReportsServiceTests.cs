using System.IO;
using System.Text.Json;
using FluentAssertions;
using Mithril.TestSupport;
using Xunit;

namespace Mithril.GameReports.Tests;

[Trait("Category", "FileIO")]
public sealed class GameReportsServiceTests : IDisposable
{
    private readonly string _dir;

    public GameReportsServiceTests()
    {
        _dir = TestPaths.CreateTempDir("gamereports-service");
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { }
    }

    [Fact]
    public void EmptyDirectory_HasNoReports()
    {
        using var svc = new GameReportsService(() => _dir);

        svc.StorageReports.Should().BeEmpty();
        svc.CharacterSnapshots.Should().BeEmpty();
        svc.GetStorageReport("Alice", "Alpha").Should().BeNull();
        svc.GetStorageContents("Alice", "Alpha").Should().BeNull();
        svc.GetCharacterSnapshot("Alice", "Alpha").Should().BeNull();
    }

    [Fact]
    public void GetStorageReport_FindsNewestForCharacter()
    {
        WriteStorageReport("Alice", "Alpha", DateTime.UtcNow.AddDays(-1));
        WriteStorageReport("Alice", "Alpha", DateTime.UtcNow);
        WriteStorageReport("Bob", "Alpha", DateTime.UtcNow);

        using var svc = new GameReportsService(() => _dir);

        var alice = svc.GetStorageReport("Alice", "Alpha");
        alice.Should().NotBeNull();
        alice!.Character.Should().Be("Alice");
        alice.Server.Should().Be("Alpha");
        // Two Alice reports exist; the newer wins (ScanForReports sorts newest first).
        alice.LastModifiedUtc.Should().BeAfter(DateTime.UtcNow.AddHours(-1));
    }

    [Fact]
    public void GetStorageReport_IsCaseInsensitive()
    {
        WriteStorageReport("Alice", "Alpha", DateTime.UtcNow);

        using var svc = new GameReportsService(() => _dir);

        svc.GetStorageReport("ALICE", "alpha").Should().NotBeNull();
        svc.GetStorageReport("alice", "ALPHA").Should().NotBeNull();
    }

    [Fact]
    public void GetStorageReport_ServerEmptyMatchesNameOnly()
    {
        WriteStorageReport("Alice", "Alpha", DateTime.UtcNow);
        WriteStorageReport("Alice", "Beta", DateTime.UtcNow.AddSeconds(1));

        using var svc = new GameReportsService(() => _dir);

        // Both match; the newer (Beta) wins because scan is newest-first.
        svc.GetStorageReport("Alice", null).Should().NotBeNull();
        svc.GetStorageReport("Alice", "").Should().NotBeNull();
    }

    [Fact]
    public void GetStorageReport_FiltersOutOtherCharacters()
    {
        WriteStorageReport("Alice", "Alpha", DateTime.UtcNow);
        WriteStorageReport("Bob", "Alpha", DateTime.UtcNow);

        using var svc = new GameReportsService(() => _dir);

        svc.GetStorageReport("Charlie", "Alpha").Should().BeNull();
        svc.GetStorageReport("Alice", "Beta").Should().BeNull();
    }

    [Fact]
    public void GetStorageContents_ParsesAndCaches()
    {
        var path = WriteStorageReport("Alice", "Alpha", DateTime.UtcNow,
            new StorageReport("Alice", "Alpha", "", "Storage", 1,
            [
                new StorageItem(42, "Apple", 3, 5, null, null, null, null,
                    true, false, null, null, null, null, null, null, null, null, null),
            ]));

        using var svc = new GameReportsService(() => _dir);

        var contents = svc.GetStorageContents("Alice", "Alpha");
        contents.Should().NotBeNull();
        contents!.Items.Should().HaveCount(1);
        contents.Items[0].Name.Should().Be("Apple");

        // Second read returns the same cached instance.
        var second = svc.GetStorageContents("Alice", "Alpha");
        second.Should().BeSameAs(contents);
    }

    [Fact]
    public void GetCharacterSnapshot_ParsesCharacterExport()
    {
        WriteCharacterExport("Alice", "Alpha");

        using var svc = new GameReportsService(() => _dir);

        var snap = svc.GetCharacterSnapshot("Alice", "Alpha");
        snap.Should().NotBeNull();
        snap!.Name.Should().Be("Alice");
        snap.Server.Should().Be("Alpha");
        snap.Skills.Should().ContainKey("Cooking");
        snap.Skills["Cooking"].Level.Should().Be(42);
    }

    [Fact]
    public void Refresh_AfterAddingFile_DiscoversIt_AndFiresEvent()
    {
        using var svc = new GameReportsService(() => _dir);
        svc.StorageReports.Should().BeEmpty();

        var fired = 0;
        svc.StorageReportsChanged += (_, _) => fired++;

        WriteStorageReport("Alice", "Alpha", DateTime.UtcNow);
        svc.Refresh();

        svc.StorageReports.Should().HaveCount(1);
        fired.Should().Be(1);
    }

    [Fact]
    public void Refresh_NoChange_DoesNotFireEvent()
    {
        WriteStorageReport("Alice", "Alpha", DateTime.UtcNow);

        using var svc = new GameReportsService(() => _dir);
        svc.StorageReports.Should().HaveCount(1);

        var fired = 0;
        svc.StorageReportsChanged += (_, _) => fired++;

        svc.Refresh();
        fired.Should().Be(0);
    }

    [Fact]
    public async Task FileSystemWatcher_DebouncesAndFires_OnNewExport()
    {
        using var svc = new GameReportsService(() => _dir);
        svc.StorageReports.Should().BeEmpty();

        var fired = new TaskCompletionSource<int>();
        svc.StorageReportsChanged += (_, _) => fired.TrySetResult(1);

        // Drop a new file. The watcher should pick it up within the debounce window.
        WriteStorageReport("Alice", "Alpha", DateTime.UtcNow);

        var winner = await Task.WhenAny(fired.Task, Task.Delay(TimeSpan.FromSeconds(5)));
        winner.Should().BeSameAs(fired.Task, "the watcher should fire StorageReportsChanged within 5s");
        svc.StorageReports.Should().HaveCount(1);
    }

    [Fact]
    public async Task FileSystemWatcher_Debounces_MultipleRapidWrites()
    {
        using var svc = new GameReportsService(() => _dir);

        var fireCount = 0;
        svc.StorageReportsChanged += (_, _) => Interlocked.Increment(ref fireCount);

        // Write the file, then touch it 5 more times in rapid succession to
        // simulate PG's chunked writes. The 500ms debounce should collapse
        // these into a single Refresh + a single event.
        var path = WriteStorageReport("Alice", "Alpha", DateTime.UtcNow);
        for (int i = 0; i < 5; i++)
        {
            File.SetLastWriteTimeUtc(path, DateTime.UtcNow);
            await Task.Delay(20);
        }

        // Wait past the debounce window.
        await Task.Delay(TimeSpan.FromSeconds(2));

        // Exact count depends on OS scheduling, but it should be small (1-2).
        // Without debouncing we'd see 5+.
        fireCount.Should().BeInRange(1, 2, "debounce should collapse rapid writes into a single event");
    }

    [Fact]
    public void GetStorageContents_InvalidatesOnMtimeChange()
    {
        var path = WriteStorageReport("Alice", "Alpha", DateTime.UtcNow.AddSeconds(-10),
            new StorageReport("Alice", "Alpha", "", "Storage", 1,
            [
                new StorageItem(1, "Apple", 1, 1, null, null, null, null,
                    true, false, null, null, null, null, null, null, null, null, null),
            ]));

        using var svc = new GameReportsService(() => _dir);

        var first = svc.GetStorageContents("Alice", "Alpha");
        first.Should().NotBeNull();
        first!.Items[0].Name.Should().Be("Apple");

        // Overwrite with different contents AND a fresher mtime.
        var fresh = new StorageReport("Alice", "Alpha", "", "Storage", 1,
        [
            new StorageItem(2, "Banana", 1, 1, null, null, null, null,
                true, false, null, null, null, null, null, null, null, null, null),
        ]);
        File.WriteAllText(path, JsonSerializer.Serialize(fresh, StorageReportJsonContext.Default.StorageReport));
        File.SetLastWriteTimeUtc(path, DateTime.UtcNow);

        // The cache should invalidate on mtime change.
        var second = svc.GetStorageContents("Alice", "Alpha");
        second.Should().NotBeNull();
        second!.Items[0].Name.Should().Be("Banana");
    }

    // ── Helpers ────────────────────────────────────────────────────────

    private string WriteStorageReport(string character, string server, DateTime mtimeUtc, StorageReport? body = null)
    {
        var stamp = mtimeUtc.ToString("yyyyMMdd_HHmmss");
        var path = Path.Combine(_dir, $"{character}_{server}_items_{stamp}.json");
        body ??= new StorageReport(character, server, "", "Storage", 1, []);
        File.WriteAllText(path, JsonSerializer.Serialize(body, StorageReportJsonContext.Default.StorageReport));
        File.SetLastWriteTimeUtc(path, mtimeUtc);
        return path;
    }

    private string WriteCharacterExport(string character, string server)
    {
        var path = Path.Combine(_dir, $"Character_{character}_{server}.json");
        // Minimal CharacterSheet shape that the parser accepts.
        var json = "{" +
            $"\"Character\":\"{character}\"," +
            $"\"ServerName\":\"{server}\"," +
            "\"Timestamp\":\"2026-04-18T13:27:12Z\"," +
            "\"Report\":\"CharacterSheet\"," +
            "\"Skills\":{\"Cooking\":{\"Level\":42,\"BonusLevels\":0,\"XpTowardNextLevel\":0,\"XpNeededForNextLevel\":1000}}," +
            "\"RecipeCompletions\":{}," +
            "\"NPCs\":{}" +
            "}";
        File.WriteAllText(path, json);
        return path;
    }
}
