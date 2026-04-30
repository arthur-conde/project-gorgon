using System.IO;
using FluentAssertions;
using Gandalf.Domain;
using Gandalf.Services;
using Mithril.Shared.Character;
using Xunit;

namespace Gandalf.Tests;

[Trait("Category", "FileIO")]
[Collection("FileIO")]
public class DerivedTimerProgressServiceTests : IDisposable
{
    private readonly string _dir;
    private readonly string _charactersDir;

    public DerivedTimerProgressServiceTests()
    {
        _dir = Mithril.TestSupport.TestPaths.CreateTempDir("gandalf_derived");
        _charactersDir = Path.Combine(_dir, "characters");
        Directory.CreateDirectory(_charactersDir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { /* best-effort */ }
    }

    private (DerivedTimerProgressService svc, PerCharacterView<DerivedProgress> view, FakeActiveCharacterService active, ManualTimeProvider time)
        BuildService()
    {
        var active = new FakeActiveCharacterService();
        var store = new PerCharacterStore<DerivedProgress>(_charactersDir, "gandalf-derived.json",
            DerivedProgressJsonContext.Default.DerivedProgress);
        var view = new PerCharacterView<DerivedProgress>(active, store);
        var time = new ManualTimeProvider(new DateTime(2026, 4, 30, 12, 0, 0, DateTimeKind.Utc));
        var svc = new DerivedTimerProgressService(view, time);
        return (svc, view, active, time);
    }

    [Fact]
    public void Start_with_past_timestamp_anchors_StartedAt_to_log_line_time()
    {
        var (svc, view, active, time) = BuildService();
        try
        {
            active.SetActiveCharacter("Arthur", "Kwatoxi");

            var ninetyMinAgo = time.GetUtcNow() - TimeSpan.FromMinutes(90);
            svc.Start("gandalf.loot", "GoblinDungeon:GoblinStaticChest1", ninetyMinAgo);

            var p = svc.GetProgress("gandalf.loot", "GoblinDungeon:GoblinStaticChest1");
            p.Should().NotBeNull();
            p!.StartedAt.Should().Be(ninetyMinAgo);
            p.DismissedAt.Should().BeNull();
        }
        finally
        {
            svc.Dispose(); view.Dispose();
        }
    }

    [Fact]
    public void Past_anchored_start_yields_correct_remaining_against_a_fixed_clock()
    {
        var (svc, view, active, time) = BuildService();
        try
        {
            active.SetActiveCharacter("Arthur", "Kwatoxi");

            var duration = TimeSpan.FromHours(3);
            var ninetyMinAgo = time.GetUtcNow() - TimeSpan.FromMinutes(90);
            svc.Start("gandalf.loot", "k1", ninetyMinAgo);

            var p = svc.GetProgress("gandalf.loot", "k1")!;
            // Compute remaining the way TimerRow does: Duration - (now - StartedAt).
            var remaining = duration - (time.GetUtcNow() - p.StartedAt);

            remaining.Should().Be(TimeSpan.FromMinutes(90));
        }
        finally
        {
            svc.Dispose(); view.Dispose();
        }
    }

    [Fact]
    public void Restart_overwrites_StartedAt_and_clears_dismissal()
    {
        var (svc, view, active, time) = BuildService();
        try
        {
            active.SetActiveCharacter("Arthur", "Kwatoxi");

            svc.Start("gandalf.loot", "k1", time.GetUtcNow() - TimeSpan.FromHours(2));
            svc.Dismiss("gandalf.loot", "k1");
            svc.GetProgress("gandalf.loot", "k1")!.DismissedAt.Should().NotBeNull();

            // Re-loot observed — fresh past-anchored stamp, dismissal cleared.
            var freshLoot = time.GetUtcNow() - TimeSpan.FromMinutes(5);
            svc.Restart("gandalf.loot", "k1", freshLoot);

            var p = svc.GetProgress("gandalf.loot", "k1")!;
            p.StartedAt.Should().Be(freshLoot);
            p.DismissedAt.Should().BeNull();
        }
        finally
        {
            svc.Dispose(); view.Dispose();
        }
    }

    [Fact]
    public void Dismiss_keeps_row_alive_so_next_observation_resurrects_it()
    {
        var (svc, view, active, time) = BuildService();
        try
        {
            active.SetActiveCharacter("Arthur", "Kwatoxi");

            svc.Start("gandalf.loot", "k1", time.GetUtcNow() - TimeSpan.FromHours(4));
            svc.Dismiss("gandalf.loot", "k1");

            // Dismissed but not deleted — the row is still queryable.
            svc.GetProgress("gandalf.loot", "k1").Should().NotBeNull();
            svc.GetProgress("gandalf.loot", "k1")!.DismissedAt.Should().Be(time.GetUtcNow());
        }
        finally
        {
            svc.Dispose(); view.Dispose();
        }
    }

    [Fact]
    public void Dismiss_uses_TimeProvider_clock_not_DateTimeOffset_UtcNow()
    {
        var (svc, view, active, time) = BuildService();
        try
        {
            active.SetActiveCharacter("Arthur", "Kwatoxi");
            svc.Start("gandalf.loot", "k1", time.GetUtcNow() - TimeSpan.FromHours(1));

            time.Advance(TimeSpan.FromMinutes(5));
            svc.Dismiss("gandalf.loot", "k1");

            // DismissedAt should match the advanced clock, not wall time.
            svc.GetProgress("gandalf.loot", "k1")!.DismissedAt.Should().Be(time.GetUtcNow());
        }
        finally
        {
            svc.Dispose(); view.Dispose();
        }
    }

    [Fact]
    public void Sources_are_namespaced_so_keys_dont_collide()
    {
        var (svc, view, active, time) = BuildService();
        try
        {
            active.SetActiveCharacter("Arthur", "Kwatoxi");

            // Same key string in two sources — must be tracked independently.
            svc.Start("gandalf.quest", "k1", time.GetUtcNow() - TimeSpan.FromMinutes(30));
            svc.Start("gandalf.loot", "k1", time.GetUtcNow() - TimeSpan.FromMinutes(10));

            svc.GetProgress("gandalf.quest", "k1")!.StartedAt
                .Should().Be(time.GetUtcNow() - TimeSpan.FromMinutes(30));
            svc.GetProgress("gandalf.loot", "k1")!.StartedAt
                .Should().Be(time.GetUtcNow() - TimeSpan.FromMinutes(10));

            svc.SnapshotFor("gandalf.quest").Should().HaveCount(1);
            svc.SnapshotFor("gandalf.loot").Should().HaveCount(1);
        }
        finally
        {
            svc.Dispose(); view.Dispose();
        }
    }

    [Fact]
    public void GarbageCollect_drops_keys_no_longer_in_catalog()
    {
        var (svc, view, active, time) = BuildService();
        try
        {
            active.SetActiveCharacter("Arthur", "Kwatoxi");

            svc.Start("gandalf.quest", "alive", time.GetUtcNow() - TimeSpan.FromHours(1));
            svc.Start("gandalf.quest", "removed", time.GetUtcNow() - TimeSpan.FromHours(1));

            svc.GarbageCollect("gandalf.quest", new[] { "alive" });

            svc.GetProgress("gandalf.quest", "alive").Should().NotBeNull();
            svc.GetProgress("gandalf.quest", "removed").Should().BeNull();
        }
        finally
        {
            svc.Dispose(); view.Dispose();
        }
    }

    [Fact]
    public void Progress_persists_across_service_restart()
    {
        var active = new FakeActiveCharacterService();
        active.SetActiveCharacter("Arthur", "Kwatoxi");
        var time = new ManualTimeProvider(new DateTime(2026, 4, 30, 12, 0, 0, DateTimeKind.Utc));
        var anchored = time.GetUtcNow() - TimeSpan.FromHours(2);

        // First run — write.
        var store1 = new PerCharacterStore<DerivedProgress>(_charactersDir, "gandalf-derived.json",
            DerivedProgressJsonContext.Default.DerivedProgress);
        var view1 = new PerCharacterView<DerivedProgress>(active, store1);
        var svc1 = new DerivedTimerProgressService(view1, time);
        svc1.Start("gandalf.loot", "k1", anchored);
        svc1.Dispose(); view1.Dispose();

        // Second run — read.
        var store2 = new PerCharacterStore<DerivedProgress>(_charactersDir, "gandalf-derived.json",
            DerivedProgressJsonContext.Default.DerivedProgress);
        var view2 = new PerCharacterView<DerivedProgress>(active, store2);
        var svc2 = new DerivedTimerProgressService(view2, time);
        try
        {
            svc2.GetProgress("gandalf.loot", "k1")!.StartedAt.Should().Be(anchored);
        }
        finally
        {
            svc2.Dispose(); view2.Dispose();
        }
    }

    private sealed class ManualTimeProvider : TimeProvider
    {
        private DateTimeOffset _now;
        public ManualTimeProvider(DateTime utcStart) =>
            _now = new DateTimeOffset(utcStart, TimeSpan.Zero);
        public override DateTimeOffset GetUtcNow() => _now;
        public void Advance(TimeSpan delta) => _now = _now.Add(delta);
    }
}
