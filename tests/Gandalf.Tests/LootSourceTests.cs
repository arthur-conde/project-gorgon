using System.IO;
using FluentAssertions;
using Gandalf.Domain;
using Gandalf.Services;
using Mithril.Shared.Character;
using Mithril.Shared.Settings;
using Xunit;

namespace Gandalf.Tests;

[Trait("Category", "FileIO")]
[Collection("FileIO")]
public class LootSourceTests : IDisposable
{
    private readonly string _dir;
    private readonly string _charactersDir;
    private readonly string _cachePath;

    public LootSourceTests()
    {
        _dir = Mithril.TestSupport.TestPaths.CreateTempDir("gandalf_loot_source");
        _charactersDir = Path.Combine(_dir, "characters");
        _cachePath = Path.Combine(_dir, "loot-catalog.json");
        Directory.CreateDirectory(_charactersDir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { /* best-effort */ }
    }

    private (LootSource src, DerivedTimerProgressService derived, FakeActiveCharacterService active, ManualTime time)
        Build(IEnumerable<DefeatCatalogEntry>? defeats = null)
    {
        var active = new FakeActiveCharacterService();
        active.SetActiveCharacter("Arthur", "Kwatoxi");
        var time = new ManualTime(new DateTime(2026, 4, 30, 12, 0, 0, DateTimeKind.Utc));

        var derivedStore = new PerCharacterStore<DerivedProgress>(_charactersDir, "gandalf-derived.json",
            DerivedProgressJsonContext.Default.DerivedProgress);
        var derivedView = new PerCharacterView<DerivedProgress>(active, derivedStore);
        var derived = new DerivedTimerProgressService(derivedView, time);

        var cacheStore = new JsonSettingsStore<LootCatalogCache>(_cachePath,
            LootCatalogCacheJsonContext.Default.LootCatalogCache);
        var cache = cacheStore.Load();
        var src = new LootSource(derived, cacheStore, cache, defeats ?? [], time);

        return (src, derived, active, time);
    }

    [Fact]
    public void First_loot_of_unknown_chest_does_not_create_a_row()
    {
        var (src, derived, _, time) = Build();
        try
        {
            // Duration unknown until rejection observed — don't fabricate one.
            src.OnChestInteraction("GoblinStaticChest1", time.GetUtcNow().UtcDateTime);

            src.Catalog.Should().BeEmpty();
            src.Progress.Should().BeEmpty();
        }
        finally
        {
            src.Dispose(); derived.Dispose();
        }
    }

    [Fact]
    public void Rejection_caches_duration_and_subsequent_loots_create_rows()
    {
        var (src, derived, _, time) = Build();
        try
        {
            // First chest: duration unknown.
            src.OnChestInteraction("GoblinStaticChest1", time.GetUtcNow().UtcDateTime);
            // Player retries — game emits the rejection screen text.
            src.OnChestCooldownObserved("GoblinStaticChest1", TimeSpan.FromHours(3));

            src.Catalog.Should().HaveCount(1);
            src.Catalog[0].Duration.Should().Be(TimeSpan.FromHours(3));

            // The first interaction's row was skipped (duration unknown then). A
            // future interaction now creates a row.
            time.Advance(TimeSpan.FromMinutes(10));
            src.OnChestInteraction("GoblinStaticChest1", time.GetUtcNow().UtcDateTime);

            src.Progress.Should().ContainKey(LootSource.ChestKey("GoblinStaticChest1"));
        }
        finally
        {
            src.Dispose(); derived.Dispose();
        }
    }

    [Fact]
    public void Past_anchored_chest_loot_yields_correct_remaining()
    {
        var (src, derived, _, time) = Build();
        try
        {
            // Seed cache so the first loot below creates a row.
            src.OnChestCooldownObserved("GoblinStaticChest1", TimeSpan.FromHours(3));

            // Loot stamped 90 minutes ago — should leave 90 min remaining.
            var past = time.GetUtcNow().UtcDateTime - TimeSpan.FromMinutes(90);
            src.OnChestInteraction("GoblinStaticChest1", past);

            var p = src.Progress[LootSource.ChestKey("GoblinStaticChest1")];
            var remaining = TimeSpan.FromHours(3) - (time.GetUtcNow() - p.StartedAt);
            remaining.Should().Be(TimeSpan.FromMinutes(90));
        }
        finally
        {
            src.Dispose(); derived.Dispose();
        }
    }

    [Fact]
    public void Defeat_kill_within_cooldown_window_is_suppressed()
    {
        var defeats = new[]
        {
            new DefeatCatalogEntry("Gazluk", "Olugax", "Olugax the Ever-Pudding", TimeSpan.FromHours(3)),
        };
        var (src, derived, _, time) = Build(defeats);
        try
        {
            // First kill — anchors cooldown.
            src.OnDefeatReward("Olugax the Ever-Pudding", time.GetUtcNow().UtcDateTime);
            var firstStart = src.Progress[LootSource.DefeatKey("Olugax")].StartedAt;

            // Second kill 30 minutes later, still inside the 3h window.
            time.Advance(TimeSpan.FromMinutes(30));
            src.OnDefeatReward("Olugax the Ever-Pudding", time.GetUtcNow().UtcDateTime);

            // StartedAt should NOT have moved — within-cooldown kills are suppressed.
            src.Progress[LootSource.DefeatKey("Olugax")].StartedAt.Should().Be(firstStart);
        }
        finally
        {
            src.Dispose(); derived.Dispose();
        }
    }

    [Fact]
    public void Defeat_kill_after_cooldown_resets_clock()
    {
        var defeats = new[]
        {
            new DefeatCatalogEntry("Gazluk", "Olugax", "Olugax the Ever-Pudding", TimeSpan.FromHours(3)),
        };
        var (src, derived, _, time) = Build(defeats);
        try
        {
            src.OnDefeatReward("Olugax the Ever-Pudding", time.GetUtcNow().UtcDateTime);

            time.Advance(TimeSpan.FromHours(4));
            src.OnDefeatReward("Olugax the Ever-Pudding", time.GetUtcNow().UtcDateTime);

            src.Progress[LootSource.DefeatKey("Olugax")].StartedAt
                .Should().Be(time.GetUtcNow());
        }
        finally
        {
            src.Dispose(); derived.Dispose();
        }
    }

    [Fact]
    public void Defeat_with_unknown_npc_creates_no_row()
    {
        var (src, derived, _, time) = Build([]);
        try
        {
            src.OnDefeatReward("Some Random Mob", time.GetUtcNow().UtcDateTime);
            src.Progress.Should().BeEmpty();
        }
        finally
        {
            src.Dispose(); derived.Dispose();
        }
    }

    [Fact]
    public void Source_id_is_stable()
    {
        var (src, derived, _, _) = Build();
        try
        {
            src.SourceId.Should().Be("gandalf.loot");
            LootSource.Id.Should().Be(src.SourceId);
        }
        finally
        {
            src.Dispose(); derived.Dispose();
        }
    }

    [Fact]
    public void Catalog_includes_both_chest_and_defeat_entries_under_one_source()
    {
        var defeats = new[]
        {
            new DefeatCatalogEntry("Gazluk", "Olugax", "Olugax the Ever-Pudding", TimeSpan.FromHours(3)),
        };
        var (src, derived, _, _) = Build(defeats);
        try
        {
            src.OnChestCooldownObserved("GoblinStaticChest1", TimeSpan.FromHours(3));

            src.Catalog.Should().HaveCount(2);
            src.Catalog.Select(c => ((LootCatalogPayload)c.SourceMetadata!).Kind)
                .Should().Contain([LootKind.Chest, LootKind.Defeat]);
        }
        finally
        {
            src.Dispose(); derived.Dispose();
        }
    }

    [Fact]
    public void Cache_persists_across_source_restart()
    {
        var active = new FakeActiveCharacterService();
        active.SetActiveCharacter("Arthur", "Kwatoxi");
        var time = new ManualTime(new DateTime(2026, 4, 30, 12, 0, 0, DateTimeKind.Utc));

        var derivedStore = new PerCharacterStore<DerivedProgress>(_charactersDir, "gandalf-derived.json",
            DerivedProgressJsonContext.Default.DerivedProgress);
        var derivedView = new PerCharacterView<DerivedProgress>(active, derivedStore);
        var derived1 = new DerivedTimerProgressService(derivedView, time);

        // Run 1: observe rejection, then dispose.
        var cacheStore1 = new JsonSettingsStore<LootCatalogCache>(_cachePath,
            LootCatalogCacheJsonContext.Default.LootCatalogCache);
        var cache1 = cacheStore1.Load();
        var src1 = new LootSource(derived1, cacheStore1, cache1, [], time);
        src1.OnChestCooldownObserved("GoblinStaticChest1", TimeSpan.FromHours(3));
        src1.Dispose();

        // Run 2: cache should already know GoblinStaticChest1's duration.
        var cacheStore2 = new JsonSettingsStore<LootCatalogCache>(_cachePath,
            LootCatalogCacheJsonContext.Default.LootCatalogCache);
        var cache2 = cacheStore2.Load();
        cache2.ChestDurationByInternalName.Should().ContainKey("GoblinStaticChest1");
        cache2.ChestDurationByInternalName["GoblinStaticChest1"].Should().Be(TimeSpan.FromHours(3));

        derived1.Dispose();
        derivedView.Dispose();
    }

    private sealed class ManualTime : TimeProvider
    {
        private DateTimeOffset _now;
        public ManualTime(DateTime utcStart) => _now = new DateTimeOffset(utcStart, TimeSpan.Zero);
        public override DateTimeOffset GetUtcNow() => _now;
        public void Advance(TimeSpan delta) => _now = _now.Add(delta);
    }
}
