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
        Build()
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
        var src = new LootSource(derived, cacheStore, cache, time);

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
            src.OnChestInteraction("GoblinStaticChest1", time.GetUtcNow().UtcDateTime);
            src.OnChestCooldownObserved("GoblinStaticChest1", TimeSpan.FromHours(3));

            src.Catalog.Should().HaveCount(1);
            src.Catalog[0].Duration.Should().Be(TimeSpan.FromHours(3));

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
            src.OnChestCooldownObserved("GoblinStaticChest1", TimeSpan.FromHours(3));

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
    public void BossKillCredit_auto_discovers_and_stamps_with_placeholder_when_uncalibrated()
    {
        var (src, derived, _, time) = Build();
        try
        {
            // No calibration overlay → falls back to PlaceholderDefeatDuration,
            // catalog row is flagged unverified.
            src.OnBossKillCredit("Den Mother", time.GetUtcNow().UtcDateTime);

            src.Catalog.Should().HaveCount(1);
            src.Catalog[0].Duration.Should().Be(LootSource.PlaceholderDefeatDuration);
            ((LootCatalogPayload)src.Catalog[0].SourceMetadata!).IsDurationVerified.Should().BeFalse();

            src.Progress.Should().ContainKey(LootSource.DefeatKey("Den Mother"));
            src.Progress[LootSource.DefeatKey("Den Mother")].StartedAt.Should().Be(time.GetUtcNow());
        }
        finally
        {
            src.Dispose(); derived.Dispose();
        }
    }

    [Fact]
    public void Calibration_overlay_supplies_verified_duration_and_area()
    {
        var (src, derived, _, time) = Build();
        try
        {
            src.OverlayDefeatCalibration(
            [
                new DefeatCatalogEntry(
                    DisplayName: "Mega-Spider",
                    RewardCooldown: TimeSpan.FromHours(2),
                    Area: "Sun Vale"),
            ]);

            src.OnBossKillCredit("Mega-Spider", time.GetUtcNow().UtcDateTime);

            src.Catalog.Should().HaveCount(1);
            src.Catalog[0].Duration.Should().Be(TimeSpan.FromHours(2));
            src.Catalog[0].Region.Should().Be("Sun Vale");

            var payload = (LootCatalogPayload)src.Catalog[0].SourceMetadata!;
            payload.IsDurationVerified.Should().BeTrue();
            payload.Region.Should().Be("Sun Vale");
        }
        finally
        {
            src.Dispose(); derived.Dispose();
        }
    }

    [Fact]
    public void Calibration_overlay_applied_after_discovery_re_projects_existing_rows()
    {
        var (src, derived, _, time) = Build();
        try
        {
            // Discovery first → placeholder row.
            src.OnBossKillCredit("Megaspider", time.GetUtcNow().UtcDateTime);
            src.Catalog[0].Duration.Should().Be(LootSource.PlaceholderDefeatDuration);

            // Calibration arrives later → existing row picks up the verified duration.
            src.OverlayDefeatCalibration(
            [
                new DefeatCatalogEntry("Megaspider", TimeSpan.FromHours(2), Area: "Sun Vale"),
            ]);

            src.Catalog.Should().HaveCount(1);
            src.Catalog[0].Duration.Should().Be(TimeSpan.FromHours(2));
            ((LootCatalogPayload)src.Catalog[0].SourceMetadata!).IsDurationVerified.Should().BeTrue();
        }
        finally
        {
            src.Dispose(); derived.Dispose();
        }
    }

    [Fact]
    public void BossKillCredit_within_window_does_not_reset_clock()
    {
        var (src, derived, _, time) = Build();
        try
        {
            src.OverlayDefeatCalibration(
            [
                new DefeatCatalogEntry("Olugax the Ever-Pudding", TimeSpan.FromHours(3)),
            ]);

            src.OnBossKillCredit("Olugax the Ever-Pudding", time.GetUtcNow().UtcDateTime);
            var firstStart = src.Progress[LootSource.DefeatKey("Olugax the Ever-Pudding")].StartedAt;

            // Server-side gate normally suppresses re-kills; idempotency check
            // protects against log replay producing the same line twice.
            src.OnBossKillCredit("Olugax the Ever-Pudding", time.GetUtcNow().UtcDateTime);

            src.Progress[LootSource.DefeatKey("Olugax the Ever-Pudding")].StartedAt.Should().Be(firstStart);
        }
        finally
        {
            src.Dispose(); derived.Dispose();
        }
    }

    [Fact]
    public void DefeatCooldownActive_is_diagnostic_only_does_not_mutate_progress()
    {
        var (src, derived, _, time) = Build();
        try
        {
            src.OnDefeatCooldownActive("Megaspider", time.GetUtcNow().UtcDateTime);
            src.Progress.Should().BeEmpty();
            src.Catalog.Should().BeEmpty();
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
        var (src, derived, _, time) = Build();
        try
        {
            src.OnChestCooldownObserved("GoblinStaticChest1", TimeSpan.FromHours(3));
            src.OnBossKillCredit("Olugax the Ever-Pudding", time.GetUtcNow().UtcDateTime);

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

        // Run 1: observe rejection + auto-discover, then dispose.
        var cacheStore1 = new JsonSettingsStore<LootCatalogCache>(_cachePath,
            LootCatalogCacheJsonContext.Default.LootCatalogCache);
        var cache1 = cacheStore1.Load();
        var src1 = new LootSource(derived1, cacheStore1, cache1, time);
        src1.OnChestCooldownObserved("GoblinStaticChest1", TimeSpan.FromHours(3));
        src1.OnBossKillCredit("Den Mother", time.GetUtcNow().UtcDateTime);
        src1.Dispose();

        // Run 2: cache should remember the chest duration and the learned boss.
        var cacheStore2 = new JsonSettingsStore<LootCatalogCache>(_cachePath,
            LootCatalogCacheJsonContext.Default.LootCatalogCache);
        var cache2 = cacheStore2.Load();
        cache2.ChestDurationByInternalName.Should().ContainKey("GoblinStaticChest1");
        cache2.ChestDurationByInternalName["GoblinStaticChest1"].Should().Be(TimeSpan.FromHours(3));
        cache2.LearnedDefeats.Should().ContainKey("Den Mother");
        cache2.LearnedDefeats["Den Mother"].FirstObservedAt.Should().Be(time.GetUtcNow().UtcDateTime);

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
