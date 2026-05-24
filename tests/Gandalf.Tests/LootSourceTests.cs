using System.IO;
using FluentAssertions;
using Gandalf.Domain;
using Gandalf.Services;
using Mithril.Shared.Character;
using Mithril.Shared.Reference;
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

    private (LootSource src, DerivedTimerProgressService derived, FakeActiveCharacterService active, ManualTime time, FakePlayerAreaState areaState)
        Build(IReferenceDataService? refData = null)
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
        var areaState = new FakePlayerAreaState();
        var src = new LootSource(derived, cacheStore, cache,
            areaState: areaState, refData: refData, time: time);

        return (src, derived, active, time, areaState);
    }

    [Fact]
    public void First_loot_of_unknown_chest_creates_unverified_placeholder_row()
    {
        var (src, derived, _, time, _) = Build();
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
        var (src, derived, _, time, _) = Build();
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
        var (src, derived, _, time, _) = Build();
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
        var (src, derived, _, time, _) = Build();
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
        var (src, derived, _, time, _) = Build();
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
        var (src, derived, _, time, _) = Build();
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
        var (src, derived, _, time, _) = Build();
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
        var (src, derived, _, time, _) = Build();
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
        var (src, derived, _, time, _) = Build();
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
        var (src, derived, _, time, _) = Build();
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
        var (src, derived, _, time, _) = Build();
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
        var (src, derived, _, time, _) = Build();
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
        var (src, derived, _, time, _) = Build();
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
        var (src, derived, _, time, _) = Build();
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
        var (src, derived, _, _, _) = Build();
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
        var (src, derived, _, time, _) = Build();
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
        var src1 = new LootSource(derived1, cacheStore1, cache1, time: time);
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
    public void Forget_chest_drops_catalog_progress_and_cache_entries()
    {
        var (src, derived, _, time, _) = Build();
        try
        {
            src.OnChestCooldownObserved("GoblinStaticChest1", TimeSpan.FromHours(3));
            src.OnChestInteraction("GoblinStaticChest1", time.GetUtcNow().UtcDateTime);

            var key = LootSource.ChestKey("GoblinStaticChest1");
            src.Catalog.Should().Contain(c => c.Key == key);
            src.Progress.Should().ContainKey(key);

            src.Forget(key);

            src.Catalog.Should().NotContain(c => c.Key == key,
                "Forget drops the catalog row entirely, not just hides it");
            src.Progress.Should().NotContainKey(key,
                "Forget removes the progress row outright (vs. Dismiss which only stamps DismissedAt)");

            // Both cache dictionaries are cleared so the row doesn't resurrect on restart.
            var cache = new JsonSettingsStore<LootCatalogCache>(_cachePath,
                LootCatalogCacheJsonContext.Default.LootCatalogCache).Load();
            cache.LearnedChests.Should().NotContainKey("GoblinStaticChest1");
            cache.ChestDurationByInternalName.Should().NotContainKey("GoblinStaticChest1");
        }
        finally
        {
            src.Dispose(); derived.Dispose();
        }
    }

    [Fact]
    public void Forget_defeat_drops_learned_entry_and_progress()
    {
        var (src, derived, _, time, _) = Build();
        try
        {
            src.OnBossKillCredit("Den Mother", time.GetUtcNow().UtcDateTime);

            var key = LootSource.DefeatKey("Den Mother");
            src.Catalog.Should().Contain(c => c.Key == key);
            src.Progress.Should().ContainKey(key);

            src.Forget(key);

            src.Catalog.Should().NotContain(c => c.Key == key);
            src.Progress.Should().NotContainKey(key);

            var cache = new JsonSettingsStore<LootCatalogCache>(_cachePath,
                LootCatalogCacheJsonContext.Default.LootCatalogCache).Load();
            cache.LearnedDefeats.Should().NotContainKey("Den Mother");
        }
        finally
        {
            src.Dispose(); derived.Dispose();
        }
    }

    [Fact]
    public void Forget_emits_single_Removed_delta()
    {
        var (src, derived, _, time, _) = Build();
        try
        {
            src.OnChestInteraction("Chair", time.GetUtcNow().UtcDateTime);
            var key = LootSource.ChestKey("Chair");

            var batches = new List<IReadOnlyList<TimerRowDelta>>();
            src.RowsChanged += (_, e) => batches.Add(e.Deltas);

            src.Forget(key);

            // Single Removed delta — the catalog re-projection drops the row;
            // the subsequent progress Remove fires ProgressChanged but produces
            // no delta because the differ only tracks rows present in the
            // (now-empty) new catalog.
            var removedDeltas = batches.SelectMany(b => b)
                .Where(d => d.Key == key && d.Kind == TimerRowChangeKind.Removed)
                .ToList();
            removedDeltas.Should().HaveCount(1);
        }
        finally
        {
            src.Dispose(); derived.Dispose();
        }
    }

    [Fact]
    public void Forget_then_re_observe_resurrects_cleanly()
    {
        var (src, derived, _, time, _) = Build();
        try
        {
            // User wipes a false-positive entry. If the parser later re-emits
            // the same key (i.e. there's a regression), the row should come
            // back — that's the deliberate non-blocklist design: re-discovery
            // is a useful regression signal.
            src.OnChestInteraction("Portal", time.GetUtcNow().UtcDateTime);
            src.Forget(LootSource.ChestKey("Portal"));
            src.Catalog.Should().BeEmpty();

            time.Advance(TimeSpan.FromMinutes(10));
            var newLoot = time.GetUtcNow().UtcDateTime;
            src.OnChestInteraction("Portal", newLoot);

            src.Catalog.Should().ContainSingle()
                .Which.Key.Should().Be(LootSource.ChestKey("Portal"));
            src.Progress[LootSource.ChestKey("Portal")].StartedAt
                .Should().Be(new DateTimeOffset(newLoot, TimeSpan.Zero));
        }
        finally
        {
            src.Dispose(); derived.Dispose();
        }
    }

    [Fact]
    public void Forget_of_unknown_key_is_noop()
    {
        var (src, derived, _, _, _) = Build();
        try
        {
            var batches = new List<IReadOnlyList<TimerRowDelta>>();
            src.RowsChanged += (_, e) => batches.Add(e.Deltas);

            src.Forget(LootSource.ChestKey("NeverExisted"));
            src.Forget(LootSource.DefeatKey("NeverExisted"));
            src.Forget("malformed-key");

            batches.Should().BeEmpty();
        }
        finally
        {
            src.Dispose(); derived.Dispose();
        }
    }

    [Fact]
    public void OnChestInteraction_emits_RowsChanged_with_added_delta()
    {
        var (src, derived, _, time, _) = Build();
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
        var (src, derived, _, time, _) = Build();
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

    // ── #178: chest area capture, friendly-name resolution, rejection-anchored rows ──

    [Fact]
    public void OnChestInteraction_stamps_current_area_on_LearnedChest()
    {
        // Post-#790: LootSource queries IPlayerAreaState.CurrentArea at
        // chest-commit time; the test double's SetArea mirrors the
        // production folder's Apply.
        var (src, derived, _, time, areaState) = Build();
        try
        {
            areaState.SetArea("AreaSerbule");
            src.OnChestInteraction("EltibuleSecretChest", time.GetUtcNow().UtcDateTime);

            var cache = new JsonSettingsStore<LootCatalogCache>(_cachePath,
                LootCatalogCacheJsonContext.Default.LootCatalogCache).Load();
            cache.LearnedChests.Should().ContainKey("EltibuleSecretChest");
            cache.LearnedChests["EltibuleSecretChest"].Area.Should().Be("AreaSerbule");
        }
        finally { src.Dispose(); derived.Dispose(); }
    }

    [Fact]
    public void OnChestInteraction_with_unknown_area_persists_null()
    {
        // No SetArea call → CurrentArea stays null → Area stamp = null.
        var (src, derived, _, time, _) = Build();
        try
        {
            src.OnChestInteraction("EltibuleSecretChest", time.GetUtcNow().UtcDateTime);

            var cache = new JsonSettingsStore<LootCatalogCache>(_cachePath,
                LootCatalogCacheJsonContext.Default.LootCatalogCache).Load();
            cache.LearnedChests["EltibuleSecretChest"].Area.Should().BeNull();
        }
        finally { src.Dispose(); derived.Dispose(); }
    }

    [Fact]
    public void OnChestInteraction_area_is_sticky_once_known()
    {
        var (src, derived, _, time, areaState) = Build();
        try
        {
            // First commit while in AreaSerbule.
            areaState.SetArea("AreaSerbule");
            src.OnChestInteraction("LootChest1", time.GetUtcNow().UtcDateTime);

            // Player ports to AreaTomb1, then loots a same-named chest. Sticky:
            // first commit's area wins so the cache doesn't ping-pong.
            areaState.SetArea("AreaTomb1");
            time.Advance(TimeSpan.FromMinutes(5));
            src.OnChestInteraction("LootChest1", time.GetUtcNow().UtcDateTime);

            var cache = new JsonSettingsStore<LootCatalogCache>(_cachePath,
                LootCatalogCacheJsonContext.Default.LootCatalogCache).Load();
            cache.LearnedChests["LootChest1"].Area.Should().Be("AreaSerbule");
        }
        finally { src.Dispose(); derived.Dispose(); }
    }

    [Fact]
    public void OnChestInteraction_stamps_area_late_when_first_commit_was_unknown()
    {
        var (src, derived, _, time, areaState) = Build();
        try
        {
            // First commit happens before any SetArea → Area is null.
            src.OnChestInteraction("EltibuleSecretChest", time.GetUtcNow().UtcDateTime);

            // Player transitions, then re-loots the same chest. Sticky-once-known
            // means we DON'T overwrite known Area, but we DO populate from null.
            areaState.SetArea("AreaEltibule");
            time.Advance(TimeSpan.FromHours(4));
            src.OnChestInteraction("EltibuleSecretChest", time.GetUtcNow().UtcDateTime);

            var cache = new JsonSettingsStore<LootCatalogCache>(_cachePath,
                LootCatalogCacheJsonContext.Default.LootCatalogCache).Load();
            cache.LearnedChests["EltibuleSecretChest"].Area.Should().Be("AreaEltibule");
        }
        finally { src.Dispose(); derived.Dispose(); }
    }

    [Fact]
    public void BuildCatalog_resolves_area_scoped_friendly_name_from_strings_all()
    {
        var refData = new Mithril.TestSupport.FakeReferenceData();
        refData.StringsRaw["npc_AreaSerbule/Cow_Moolanda_Name"] = "Wanda";
        refData.StringsRaw["npc_AreaSerbule/Cow_Bessie_Name"] = "Bessie";

        var (src, derived, _, time, areaState) = Build(refData);
        try
        {
            areaState.SetArea("AreaSerbule");
            src.OnChestInteraction("Cow_Moolanda", time.GetUtcNow().UtcDateTime);
            src.OnChestInteraction("Cow_Bessie", time.GetUtcNow().UtcDateTime);

            var moolanda = src.Catalog.Single(c => c.Key == LootSource.ChestKey("Cow_Moolanda"));
            var bessie = src.Catalog.Single(c => c.Key == LootSource.ChestKey("Cow_Bessie"));

            moolanda.DisplayName.Should().Be("Wanda");
            bessie.DisplayName.Should().Be("Bessie");
        }
        finally { src.Dispose(); derived.Dispose(); }
    }

    [Fact]
    public void BuildCatalog_falls_back_to_no_area_form_when_area_scoped_missing()
    {
        var refData = new Mithril.TestSupport.FakeReferenceData();
        refData.StringsRaw["npc_LootBackpack1_Name"] = "Adventurer's Pack";
        // No npc_AreaSerbule/LootBackpack1_Name entry — global prefab.

        var (src, derived, _, time, areaState) = Build(refData);
        try
        {
            areaState.SetArea("AreaSerbule");
            src.OnChestInteraction("LootBackpack1", time.GetUtcNow().UtcDateTime);
            var entry = src.Catalog.Single(c => c.Key == LootSource.ChestKey("LootBackpack1"));
            entry.DisplayName.Should().Be("Adventurer's Pack");
        }
        finally { src.Dispose(); derived.Dispose(); }
    }

    [Fact]
    public void BuildCatalog_falls_back_to_internal_name_when_strings_all_misses_entirely()
    {
        var refData = new Mithril.TestSupport.FakeReferenceData(); // empty Strings
        var (src, derived, _, time, _) = Build(refData: refData);
        try
        {
            src.OnChestInteraction("UnknownChest1", time.GetUtcNow().UtcDateTime);
            var entry = src.Catalog.Single(c => c.Key == LootSource.ChestKey("UnknownChest1"));
            entry.DisplayName.Should().Be("UnknownChest1");
        }
        finally { src.Dispose(); derived.Dispose(); }
    }

    [Fact]
    public void BuildCatalog_uses_area_friendly_name_for_region()
    {
        var refData = new Mithril.TestSupport.FakeReferenceData();
        refData.AreasRaw["AreaSerbule"] = new AreaEntry("AreaSerbule", "Serbule", "Serbule");

        var (src, derived, _, time, areaState) = Build(refData);
        try
        {
            areaState.SetArea("AreaSerbule");
            src.OnChestInteraction("EltibuleSecretChest", time.GetUtcNow().UtcDateTime);
            var entry = src.Catalog.Single(c => c.Key == LootSource.ChestKey("EltibuleSecretChest"));
            entry.Region.Should().Be("Serbule");
        }
        finally { src.Dispose(); derived.Dispose(); }
    }

    [Fact]
    public void Cow_rejection_with_no_prior_row_anchors_at_rejection_time()
    {
        var (src, derived, _, time, _) = Build();
        try
        {
            var rejectionAt = time.GetUtcNow().UtcDateTime;
            src.OnChestCooldownObserved(
                "Cow_Elsie", TimeSpan.FromHours(1),
                rejectionAt, anchorFromRejection: true);

            // Row created at rejection time → countdown is full duration ahead.
            src.Progress.Should().ContainKey(LootSource.ChestKey("Cow_Elsie"));
            src.Progress[LootSource.ChestKey("Cow_Elsie")].StartedAt
                .Should().Be(new DateTimeOffset(rejectionAt, TimeSpan.Zero));

            // LearnedChests entry persists so the row survives restart.
            var cache = new JsonSettingsStore<LootCatalogCache>(_cachePath,
                LootCatalogCacheJsonContext.Default.LootCatalogCache).Load();
            cache.LearnedChests.Should().ContainKey("Cow_Elsie");
        }
        finally { src.Dispose(); derived.Dispose(); }
    }

    [Fact]
    public void Cow_rejection_leaves_active_prior_row_alone()
    {
        var (src, derived, _, time, _) = Build();
        try
        {
            // Successful milking 10 min ago.
            var milkAt = time.GetUtcNow().UtcDateTime;
            src.OnChestInteraction("Cow_Bessie", milkAt);
            time.Advance(TimeSpan.FromMinutes(10));

            // Rejection arrives. Cooldown is 1 h, so row is still in flight
            // (FiringAt = milkAt + 1 h, which is still 50 min in the future).
            // Anchor must NOT be refreshed.
            var rejectionAt = time.GetUtcNow().UtcDateTime;
            src.OnChestCooldownObserved(
                "Cow_Bessie", TimeSpan.FromHours(1),
                rejectionAt, anchorFromRejection: true);

            src.Progress[LootSource.ChestKey("Cow_Bessie")].StartedAt
                .Should().Be(new DateTimeOffset(milkAt, TimeSpan.Zero),
                    "active row's anchor must not be refreshed by rejection");
        }
        finally { src.Dispose(); derived.Dispose(); }
    }

    [Fact]
    public void Cow_rejection_refreshes_stale_prior_anchor_to_rejection_time()
    {
        var (src, derived, _, time, _) = Build();
        try
        {
            // Milking 2 h ago — cooldown of 1 h would have made the row Done at +1 h.
            var milkAt = time.GetUtcNow().UtcDateTime;
            src.OnChestInteraction("Cow_Bessie", milkAt);
            // Pre-seed the verified duration so prior+duration is 1 h after milkAt.
            src.OnChestCooldownObserved("Cow_Bessie", TimeSpan.FromHours(1));
            time.Advance(TimeSpan.FromHours(2));

            // Rejection now — row's FiringAt = milkAt + 1 h is 1 h in the past
            // (stale). Refresh anchor to rejection time so the user sees a
            // fresh 1 h countdown rather than a stale "Done" badge.
            var rejectionAt = time.GetUtcNow().UtcDateTime;
            src.OnChestCooldownObserved(
                "Cow_Bessie", TimeSpan.FromHours(1),
                rejectionAt, anchorFromRejection: true);

            src.Progress[LootSource.ChestKey("Cow_Bessie")].StartedAt
                .Should().Be(new DateTimeOffset(rejectionAt, TimeSpan.Zero));
        }
        finally { src.Dispose(); derived.Dispose(); }
    }

    [Fact]
    public void Cow_rejection_replays_idempotently_when_row_still_active()
    {
        var (src, derived, _, time, _) = Build();
        try
        {
            // Milking 10 min ago, 1 h cooldown — row is still in flight.
            var milkAt = time.GetUtcNow().UtcDateTime;
            src.OnChestInteraction("Cow_Bessie", milkAt);
            time.Advance(TimeSpan.FromMinutes(10));

            // Rejection at the same timestamp (replay scenario) — idempotency
            // via the active-row branch (anchor unchanged).
            var rejectionAt = time.GetUtcNow().UtcDateTime;
            src.OnChestCooldownObserved(
                "Cow_Bessie", TimeSpan.FromHours(1),
                rejectionAt, anchorFromRejection: true);
            src.OnChestCooldownObserved(
                "Cow_Bessie", TimeSpan.FromHours(1),
                rejectionAt, anchorFromRejection: true);

            src.Progress[LootSource.ChestKey("Cow_Bessie")].StartedAt
                .Should().Be(new DateTimeOffset(milkAt, TimeSpan.Zero));
        }
        finally { src.Dispose(); derived.Dispose(); }
    }

    [Fact]
    public void Chest_rejection_default_does_not_refresh_anchor()
    {
        // Forward-looking chest grammar gives us absolute info — the row's
        // existing StartedAt is more accurate than the rejection timestamp.
        // Default anchorFromRejection=false preserves today's behaviour.
        var (src, derived, _, time, _) = Build();
        try
        {
            var lootAt = time.GetUtcNow().UtcDateTime;
            src.OnChestInteraction("EltibuleSecretChest", lootAt);
            time.Advance(TimeSpan.FromHours(4));

            var rejectionAt = time.GetUtcNow().UtcDateTime;
            src.OnChestCooldownObserved(
                "EltibuleSecretChest", TimeSpan.FromHours(3), rejectionAt);

            src.Progress[LootSource.ChestKey("EltibuleSecretChest")].StartedAt
                .Should().Be(new DateTimeOffset(lootAt, TimeSpan.Zero),
                    "chest grammar: rejection only updates duration, never the anchor");
        }
        finally { src.Dispose(); derived.Dispose(); }
    }

}
