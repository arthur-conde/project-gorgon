using System.IO;
using FluentAssertions;
using Gandalf.Domain;
using Gandalf.Services;
using Mithril.Shared.Character;
using Xunit;

namespace Gandalf.Tests;

/// <summary>
/// One-shot importer for the pre-#718 per-character <c>quests.json</c>
/// completion-history map. Lifts the legacy entries into
/// <see cref="DerivedTimerProgressService"/> rows and retires the source
/// file so subsequent startups no-op.
/// </summary>
[Trait("Category", "FileIO")]
[Collection("FileIO")]
public sealed class QuestCompletionImportServiceTests : IDisposable
{
    private readonly string _dir;
    private readonly string _charactersDir;

    public QuestCompletionImportServiceTests()
    {
        _dir = Mithril.TestSupport.TestPaths.CreateTempDir("gandalf_quest_import");
        _charactersDir = Path.Combine(_dir, "characters");
        Directory.CreateDirectory(_charactersDir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { /* best-effort */ }
    }

    private string SeedLegacyQuestsJson(string character, string server, string json)
    {
        var charDir = Path.Combine(_charactersDir, $"{character}_{server}");
        Directory.CreateDirectory(charDir);
        var path = Path.Combine(charDir, "quests.json");
        File.WriteAllText(path, json);
        return path;
    }

    private (QuestCompletionImportService importer,
             PerCharacterStore<DerivedProgress> derivedStore,
             PerCharacterView<DerivedProgress> derivedView,
             FakeActiveCharacterService active)
        Build(string activeChar = "Arthur", string activeServer = "Kwatoxi")
    {
        var derivedStore = new PerCharacterStore<DerivedProgress>(_charactersDir, "gandalf-derived.json",
            DerivedProgressJsonContext.Default.DerivedProgress);
        var active = new FakeActiveCharacterService();
        active.SetActiveCharacter(activeChar, activeServer);
        var derivedView = new PerCharacterView<DerivedProgress>(active, derivedStore);

        var importer = new QuestCompletionImportService(
            new PerCharacterStoreOptions { CharactersRootDir = _charactersDir },
            derivedStore, derivedView, active);
        return (importer, derivedStore, derivedView, active);
    }

    [Fact]
    public async Task ImportsCompletionHistoryEntriesIntoDerivedProgress()
    {
        SeedLegacyQuestsJson("Arthur", "Kwatoxi", """
        {
          "schemaVersion": 1,
          "activeQuests": {},
          "completionHistory": {
            "Quest_Daily_50208": {
              "internalName": "Quest_Daily_50208",
              "lastCompletedAt": "2026-05-18T10:00:00+00:00"
            },
            "Quest_Daily_14003": {
              "internalName": "Quest_Daily_14003",
              "lastCompletedAt": "2026-05-19T11:30:00+00:00"
            }
          }
        }
        """);

        var (importer, derivedStore, derivedView, _) = Build();
        try
        {
            await importer.StartAsync(CancellationToken.None);

            var derived = derivedStore.Load("Arthur", "Kwatoxi");
            derived.BySource.Should().ContainKey(QuestSource.Id);
            var quests = derived.BySource[QuestSource.Id];
            quests.Should().ContainKey(QuestSource.QuestKey("Quest_Daily_50208"));
            quests.Should().ContainKey(QuestSource.QuestKey("Quest_Daily_14003"));
            quests[QuestSource.QuestKey("Quest_Daily_50208")].StartedAt
                .Should().Be(new DateTimeOffset(2026, 5, 18, 10, 0, 0, TimeSpan.Zero));
            quests[QuestSource.QuestKey("Quest_Daily_14003")].StartedAt
                .Should().Be(new DateTimeOffset(2026, 5, 19, 11, 30, 0, TimeSpan.Zero));
        }
        finally { derivedView.Dispose(); }
    }

    [Fact]
    public async Task RetiresSourceFileAfterImport()
    {
        var legacyPath = SeedLegacyQuestsJson("Arthur", "Kwatoxi", """
        {
          "schemaVersion": 1,
          "activeQuests": {},
          "completionHistory": {
            "Quest_Daily_50208": {
              "internalName": "Quest_Daily_50208",
              "lastCompletedAt": "2026-05-18T10:00:00+00:00"
            }
          }
        }
        """);

        var (importer, _, derivedView, _) = Build();
        try
        {
            await importer.StartAsync(CancellationToken.None);

            File.Exists(legacyPath).Should().BeFalse("the source file is renamed after import");
            File.Exists(legacyPath + ".migrated").Should().BeTrue(
                "the source content is preserved with a .migrated suffix as a safety net");
        }
        finally { derivedView.Dispose(); }
    }

    [Fact]
    public async Task IsIdempotentAcrossRestarts()
    {
        SeedLegacyQuestsJson("Arthur", "Kwatoxi", """
        {
          "schemaVersion": 1,
          "activeQuests": {},
          "completionHistory": {
            "Quest_Daily_50208": {
              "internalName": "Quest_Daily_50208",
              "lastCompletedAt": "2026-05-18T10:00:00+00:00"
            }
          }
        }
        """);

        // First run: imports + retires.
        {
            var (importer, _, derivedView, _) = Build();
            try { await importer.StartAsync(CancellationToken.None); }
            finally { derivedView.Dispose(); }
        }

        // Second run: no source file → no-op (must not throw, must not alter
        // the previously-imported derived rows).
        {
            var (importer, derivedStore, derivedView, _) = Build();
            try
            {
                await importer.StartAsync(CancellationToken.None);

                var derived = derivedStore.Load("Arthur", "Kwatoxi");
                derived.BySource[QuestSource.Id].Should().ContainKey(QuestSource.QuestKey("Quest_Daily_50208"));
                derived.BySource[QuestSource.Id][QuestSource.QuestKey("Quest_Daily_50208")].StartedAt
                    .Should().Be(new DateTimeOffset(2026, 5, 18, 10, 0, 0, TimeSpan.Zero));
            }
            finally { derivedView.Dispose(); }
        }
    }

    [Fact]
    public async Task ImportsAcrossMultipleCharacters()
    {
        SeedLegacyQuestsJson("Arthur", "Kwatoxi", """
        {
          "schemaVersion": 1,
          "completionHistory": {
            "Quest_A": { "internalName": "Quest_A", "lastCompletedAt": "2026-05-18T10:00:00+00:00" }
          }
        }
        """);
        SeedLegacyQuestsJson("Bob", "Kwatoxi", """
        {
          "schemaVersion": 1,
          "completionHistory": {
            "Quest_B": { "internalName": "Quest_B", "lastCompletedAt": "2026-05-19T11:00:00+00:00" }
          }
        }
        """);

        var (importer, derivedStore, derivedView, _) = Build();
        try
        {
            await importer.StartAsync(CancellationToken.None);

            derivedStore.Load("Arthur", "Kwatoxi").BySource[QuestSource.Id]
                .Should().ContainKey(QuestSource.QuestKey("Quest_A"));
            derivedStore.Load("Bob", "Kwatoxi").BySource[QuestSource.Id]
                .Should().ContainKey(QuestSource.QuestKey("Quest_B"));
        }
        finally { derivedView.Dispose(); }
    }

    [Fact]
    public async Task EmptyCompletionHistoryStillRetiresSourceFile()
    {
        var legacyPath = SeedLegacyQuestsJson("Arthur", "Kwatoxi", """
        {
          "schemaVersion": 1,
          "activeQuests": {
            "Quest_X": { "internalName": "Quest_X", "acceptedAt": "2026-05-18T10:00:00+00:00" }
          },
          "completionHistory": {}
        }
        """);

        var (importer, _, derivedView, _) = Build();
        try
        {
            await importer.StartAsync(CancellationToken.None);

            File.Exists(legacyPath).Should().BeFalse(
                "even an empty-history file is retired so we don't re-read on every startup");
            File.Exists(legacyPath + ".migrated").Should().BeTrue();
        }
        finally { derivedView.Dispose(); }
    }

    [Fact]
    public async Task PreservesExistingDerivedRowsForOtherSources()
    {
        // Seed a pre-existing gandalf-derived.json with a loot-source row to
        // make sure the importer doesn't overwrite the whole file.
        var charDir = Path.Combine(_charactersDir, "Arthur_Kwatoxi");
        Directory.CreateDirectory(charDir);
        var derivedStorePre = new PerCharacterStore<DerivedProgress>(_charactersDir, "gandalf-derived.json",
            DerivedProgressJsonContext.Default.DerivedProgress);
        var existing = new DerivedProgress();
        existing.BySource["gandalf.loot"] = new Dictionary<string, DerivedTimerProgress>(StringComparer.Ordinal)
        {
            ["chest:my-chest"] = new DerivedTimerProgress
            {
                StartedAt = new DateTimeOffset(2026, 5, 17, 8, 0, 0, TimeSpan.Zero),
                DismissedAt = null,
            },
        };
        derivedStorePre.Save("Arthur", "Kwatoxi", existing);

        SeedLegacyQuestsJson("Arthur", "Kwatoxi", """
        {
          "schemaVersion": 1,
          "completionHistory": {
            "Quest_A": { "internalName": "Quest_A", "lastCompletedAt": "2026-05-18T10:00:00+00:00" }
          }
        }
        """);

        var (importer, derivedStore, derivedView, _) = Build();
        try
        {
            await importer.StartAsync(CancellationToken.None);

            var derived = derivedStore.Load("Arthur", "Kwatoxi");
            derived.BySource.Should().ContainKey("gandalf.loot");
            derived.BySource["gandalf.loot"].Should().ContainKey("chest:my-chest");
            derived.BySource.Should().ContainKey(QuestSource.Id);
            derived.BySource[QuestSource.Id].Should().ContainKey(QuestSource.QuestKey("Quest_A"));
        }
        finally { derivedView.Dispose(); }
    }

    [Fact]
    public async Task NoSourceFile_NoOps()
    {
        // No quests.json anywhere → importer should silently no-op without
        // touching the derived store.
        var (importer, derivedStore, derivedView, _) = Build();
        try
        {
            await importer.StartAsync(CancellationToken.None);

            var derived = derivedStore.Load("Arthur", "Kwatoxi");
            derived.BySource.Should().NotContainKey(QuestSource.Id);
        }
        finally { derivedView.Dispose(); }
    }
}
