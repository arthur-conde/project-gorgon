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
    public void First_loot_of_unknown_chest_creates_unverified_placeholder_row()
    {
        var (src, derived, _, time) = Build();
        try
        {
            // No rejection observed yet → row stamps with PlaceholderChestDuration,
            // catalog row flagged unverified. The user sees an immediate timer
            // instead of silently losing the cooldown.
            var lootTime = time.GetUtcNow().UtcDateTime;
            src.OnChestInteraction("GoblinStaticChest1", lootTime);

            src.Catalog.Should().HaveCount(1);
            src.Catalog[0].Duration.Should().Be(LootSource.PlaceholderChestDuration);
            ((LootCatalogPayload)src.Catalog[0].SourceMetadata!).IsDurationVerified.Should().BeFalse();

            src.Progress.Should().ContainKey(LootSource.ChestKey("GoblinStaticChest1"));
            src.Progress[LootSource.ChestKey("GoblinStaticChest1")].StartedAt
                .Should().Be(new DateTimeOffset(lootTime, TimeSpan.Zero));
        }
        finally
        {
            src.Dispose(); derived.Dispose();
        }
    }

    [Fact]
    public void Rejection_after_placeholder_loot_upgrades_duration_and_preserves_StartedAt()
    {
        var (src, derived, _, time) = Build();
        try
        {
            // Loot at T0 with no prior rejection → placeholder row anchored at T0.
            var lootTime = time.GetUtcNow().UtcDateTime;
            src.OnChestInteraction("EltibuleSecretChest", lootTime);

            var key = LootSource.ChestKey("EltibuleSecretChest");
            var anchored = src.Progress[key].StartedAt;

            // Rejection arrives later carrying the real duration. The row's
            // anchor must remain at the original ProcessStartInteraction
            // timestamp; only the catalog Duration upgrades.
            time.Advance(TimeSpan.FromMinutes(45));
            src.OnChestCooldownObserved("EltibuleSecretChest", TimeSpan.FromHours(6));

            src.Progress[key].StartedAt.Should().Be(anchored,
                "rejection learns the duration but the cooldown is still anchored on the original loot");

            src.Catalog.Should().HaveCount(1);
            src.Catalog[0].Duration.Should().Be(TimeSpan.FromHours(6));
            ((LootCatalogPayload)src.Catalog[0].SourceMetadata!).IsDurationVerified.Should().BeTrue();
        }
        finally
        {
            src.Dispose(); derived.Dispose();
        }
    }

    [Fact]
    public void Rejection_with_shorter_real_duration_fires_TimerReady_when_already_elapsed()
    {
        var (src, derived, _, time) = Build();
        try
        {
            var lootTime = time.GetUtcNow().UtcDateTime;
            src.OnChestInteraction("GoblinStaticChest1", lootTime);

            var fired = new List<TimerReadyEventArgs>();
            src.TimerReady += (_, e) => fired.Add(e);

            // Real duration (15 min) is shorter than the 3 h placeholder, and
            // we've already advanced 30 min — the row is genuinely ready now.
            time.Advance(TimeSpan.FromMinutes(30));
            src.OnChestCooldownObserved("GoblinStaticChest1", TimeSpan.FromMinutes(15));

            fired.Should().ContainSingle()
                .Which.Key.Should().Be(LootSource.ChestKey("GoblinStaticChest1"));
        }
        finally
        {
            src.Dispose(); derived.Dispose();
        }
    }

    [Fact]
    public void Rejection_after_placeholder_loot_does_not_refire_when_still_cooling()
    {
        var (src, derived, _, time) = Build();
        try
        {
            src.OnChestInteraction("GoblinStaticChest1", time.GetUtcNow().UtcDateTime);

            var fired = new List<TimerReadyEventArgs>();
            src.TimerReady += (_, e) => fired.Add(e);

            // Real duration (6 h) is longer than the placeholder; the row is
            // still cooling, no ready fire expected.
            src.OnChestCooldownObserved("GoblinStaticChest1", TimeSpan.FromHours(6));

            fired.Should().BeEmpty();
        }
        finally
        {
            src.Dispose(); derived.Dispose();
        }
    }

    [Fact]
    public void Subsequent_loot_after_rejection_uses_verified_duration()
    {
        var (src, derived, _, time) = Build();
        try
        {
            src.OnChestInteraction("GoblinStaticChest1", time.GetUtcNow().UtcDateTime);
            src.OnChestCooldownObserved("GoblinStaticChest1", TimeSpan.FromHours(3));

            time.Advance(TimeSpan.FromHours(4));
            var newLoot = time.GetUtcNow().UtcDateTime;
            src.OnChestInteraction("GoblinStaticChest1", newLoot);

            var key = LootSource.ChestKey("GoblinStaticChest1");
            src.Progress[key].StartedAt.Should().Be(new DateTimeOffset(newLoot, TimeSpan.Zero));
            src.Catalog.Single(c => c.Key == key).Duration.Should().Be(TimeSpan.FromHours(3));
            ((LootCatalogPayload)src.Catalog.Single(c => c.Key == key).SourceMetadata!)
                .IsDurationVerified.Should().BeTrue();
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
    public void Replay_of_dismissed_boss_kill_preserves_dismissal()
    {
        var (src, derived, _, time) = Build();
        try
        {
            var killTime = time.GetUtcNow().UtcDateTime;
            src.OnBossKillCredit("Megaspider", killTime);

            var key = LootSource.DefeatKey("Megaspider");
            derived.Dismiss(LootSource.Id, key);
            src.Progress[key].DismissedAt.Should().NotBeNull();

            // Same line replays (e.g. Mithril restarts mid-session and
            // PlayerLogStream re-feeds the wisdom-credit line). Dismissal
            // must survive.
            src.OnBossKillCredit("Megaspider", killTime);
            src.Progress[key].DismissedAt.Should().NotBeNull(
                "replay of the same StartedAt must not undo the user's dismissal");
        }
        finally
        {
            src.Dispose(); derived.Dispose();
        }
    }

    [Fact]
    public void Genuine_re_kill_after_dismissal_resurrects_the_row()
    {
        var (src, derived, _, time) = Build();
        try
        {
            src.OnBossKillCredit("Megaspider", time.GetUtcNow().UtcDateTime);

            var key = LootSource.DefeatKey("Megaspider");
            derived.Dismiss(LootSource.Id, key);

            // A *new* kill (different timestamp) is not a replay — the player
            // legitimately re-killed the boss after the cooldown elapsed and
            // the row should resurrect with a fresh clock.
            time.Advance(TimeSpan.FromHours(4));
            var newKill = time.GetUtcNow().UtcDateTime;
            src.OnBossKillCredit("Megaspider", newKill);

            src.Progress[key].StartedAt.Should().Be(time.GetUtcNow());
            src.Progress[key].DismissedAt.Should().BeNull();
        }
        finally
        {
            src.Dispose(); derived.Dispose();
        }
    }

    [Fact]
    public void Replay_of_dismissed_chest_loot_preserves_dismissal()
    {
        var (src, derived, _, time) = Build();
        try
        {
            src.OnChestCooldownObserved("GoblinStaticChest1", TimeSpan.FromHours(3));
            var lootTime = time.GetUtcNow().UtcDateTime;
            src.OnChestInteraction("GoblinStaticChest1", lootTime);

            var key = LootSource.ChestKey("GoblinStaticChest1");
            derived.Dismiss(LootSource.Id, key);

            // Same chest line replays — dismissal must survive.
            src.OnChestInteraction("GoblinStaticChest1", lootTime);
            src.Progress[key].DismissedAt.Should().NotBeNull();
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

    [Fact]
    public void OnChestInteraction_emits_RowsChanged_with_added_delta()
    {
        var (src, derived, _, time) = Build();
        try
        {
            var batches = new List<IReadOnlyList<TimerRowDelta>>();
            src.RowsChanged += (_, e) => batches.Add(e.Deltas);

            src.OnChestInteraction("GoblinStaticChest1", time.GetUtcNow().UtcDateTime);

            // EnsureCatalogReprojected (Added with null progress) followed by
            // _derived.Start (ProgressChanged with the new progress). Two batches
            // for one logical interaction is acceptable; the binder applies them
            // sequentially without visible flicker.
            batches.Should().NotBeEmpty();
            var addedDelta = batches.SelectMany(b => b)
                .FirstOrDefault(d => d.Key == LootSource.ChestKey("GoblinStaticChest1")
                                     && d.Kind == TimerRowChangeKind.Added);
            addedDelta.Should().NotBeNull();
            addedDelta!.Catalog.Should().NotBeNull();

            var progressDelta = batches.SelectMany(b => b)
                .FirstOrDefault(d => d.Key == LootSource.ChestKey("GoblinStaticChest1")
                                     && d.Kind == TimerRowChangeKind.ProgressChanged);
            progressDelta.Should().NotBeNull();
            progressDelta!.Progress.Should().NotBeNull();
        }
        finally { src.Dispose(); derived.Dispose(); }
    }

    [Fact]
    public void OverlayDefeatCalibration_emits_one_batched_RowsChanged()
    {
        var (src, derived, _, time) = Build();
        try
        {
            // Pre-populate the catalog with a few learned defeats so the overlay
            // refresh produces a meaningful diff.
            src.OnBossKillCredit("Den Mother", time.GetUtcNow().UtcDateTime);
            src.OnBossKillCredit("Olugax", time.GetUtcNow().UtcDateTime);

            var batches = new List<IReadOnlyList<TimerRowDelta>>();
            src.RowsChanged += (_, e) => batches.Add(e.Deltas);

            // Single overlay refresh updates region + duration for both defeats.
            src.OverlayDefeatCalibration(new[]
            {
                new DefeatCatalogEntry("Den Mother", TimeSpan.FromHours(6), "Sun Vale"),
                new DefeatCatalogEntry("Olugax", TimeSpan.FromHours(6), "Sun Vale"),
            });

            // The whole calibration applies in a single RowsChanged event with
            // N deltas (not N events). This is the contract the binder relies on
            // to call ICollectionView.Refresh once per batch.
            batches.Should().HaveCount(1);
            batches[0].Should().HaveCount(2);
            batches[0].Should().AllSatisfy(d =>
                d.Kind.Should().Be(TimerRowChangeKind.CatalogChanged));
        }
        finally { src.Dispose(); derived.Dispose(); }
    }

    private sealed class ManualTime : TimeProvider
    {
        private DateTimeOffset _now;
        public ManualTime(DateTime utcStart) => _now = new DateTimeOffset(utcStart, TimeSpan.Zero);
        public override DateTimeOffset GetUtcNow() => _now;
        public void Advance(TimeSpan delta) => _now = _now.Add(delta);
    }
}
