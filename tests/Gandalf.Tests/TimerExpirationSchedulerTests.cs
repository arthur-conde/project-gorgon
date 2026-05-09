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
public class TimerExpirationSchedulerTests : IDisposable
{
    private readonly string _dir;
    private readonly string _defsPath;
    private readonly string _charactersDir;

    public TimerExpirationSchedulerTests()
    {
        _dir = Mithril.TestSupport.TestPaths.CreateTempDir("gandalf_expiration_scheduler");
        _defsPath = Path.Combine(_dir, "definitions.json");
        _charactersDir = Path.Combine(_dir, "characters");
        Directory.CreateDirectory(_charactersDir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { /* best-effort */ }
    }

    private (TimerExpirationScheduler scheduler, TimerProgressService progress, TimerDefinitionsService defs, ManualTime time)
        Build()
    {
        var defStore = new JsonSettingsStore<GandalfDefinitions>(_defsPath,
            GandalfDefinitionsJsonContext.Default.GandalfDefinitions);
        var defs = defStore.Load();
        var defsSvc = new TimerDefinitionsService(defStore, defs);

        var active = new FakeActiveCharacterService();
        active.SetActiveCharacter("Arthur", "Kwatoxi");
        var store = new PerCharacterStore<GandalfProgress>(_charactersDir, "gandalf.json",
            GandalfProgressJsonContext.Default.GandalfProgress);
        var view = new PerCharacterView<GandalfProgress>(active, store);
        var progressSvc = new TimerProgressService(view, defsSvc,
            new PerCharacterStoreOptions { CharactersRootDir = _charactersDir });

        var time = new ManualTime(new DateTime(2026, 4, 30, 12, 0, 0, DateTimeKind.Utc));
        var scheduler = new TimerExpirationScheduler(progressSvc, defsSvc, time);
        return (scheduler, progressSvc, defsSvc, time);
    }

    [Fact]
    public void NextExpirationAt_is_null_when_no_timers_running()
    {
        var (sched, progress, defs, _) = Build();
        try
        {
            sched.NextExpirationAt.Should().BeNull();

            defs.Add(new GandalfTimerDef { Name = "Idle", Duration = TimeSpan.FromHours(1) });
            sched.NextExpirationAt.Should().BeNull("a defined-but-not-started timer doesn't expire");
        }
        finally
        {
            sched.Dispose(); progress.Dispose(); defs.Dispose();
        }
    }

    [Fact]
    public void NextExpirationAt_picks_soonest_StartedAt_plus_Duration()
    {
        var (sched, progress, defs, _) = Build();
        try
        {
            // Started in the past; one expires sooner than the other.
            // Use real wall-clock UtcNow because TimerProgressService.Start
            // stamps StartedAt = DateTimeOffset.UtcNow internally — not
            // an injected clock. Both timers start ~now; the 30-second one
            // expires first.
            defs.Add(new GandalfTimerDef { Name = "Soon", Duration = TimeSpan.FromSeconds(30) });
            defs.Add(new GandalfTimerDef { Name = "Later", Duration = TimeSpan.FromHours(2) });
            var soonId = defs.Definitions[0].Id;
            var laterId = defs.Definitions[1].Id;

            progress.Start(soonId);
            progress.Start(laterId);

            var soon = progress.GetProgress(soonId);
            soon!.StartedAt.Should().NotBeNull();
            var expectedSoonest = soon.StartedAt!.Value + TimeSpan.FromSeconds(30);

            sched.NextExpirationAt.Should().BeCloseTo(expectedSoonest, TimeSpan.FromSeconds(1));
        }
        finally
        {
            sched.Dispose(); progress.Dispose(); defs.Dispose();
        }
    }

    [Fact]
    public void Tick_runs_CheckExpirations_and_fires_TimerExpired_for_past_due_rows()
    {
        var (sched, progress, defs, _) = Build();
        try
        {
            defs.Add(new GandalfTimerDef
            {
                Name = "Quick",
                Duration = TimeSpan.FromMilliseconds(1),
            });
            var id = defs.Definitions[0].Id;
            progress.Start(id);

            // Wait past the duration so CheckExpirations sees the row as Done.
            // The TimerProgressService uses real wall-clock for state checks.
            System.Threading.Thread.Sleep(20);

            var expired = new List<TimerExpiredEventArgs>();
            progress.TimerExpired += (_, e) => expired.Add(e);

            sched.TickForTests();

            expired.Should().ContainSingle();
            expired[0].Def.Id.Should().Be(id);
            progress.GetProgress(id)!.CompletedAt.Should().NotBeNull();
        }
        finally
        {
            sched.Dispose(); progress.Dispose(); defs.Dispose();
        }
    }

    [Fact]
    public void Removing_a_definition_clears_NextExpirationAt()
    {
        var (sched, progress, defs, _) = Build();
        try
        {
            defs.Add(new GandalfTimerDef { Name = "X", Duration = TimeSpan.FromHours(1) });
            var id = defs.Definitions[0].Id;
            progress.Start(id);

            sched.NextExpirationAt.Should().NotBeNull();

            defs.Remove(id);
            sched.NextExpirationAt.Should().BeNull();
        }
        finally
        {
            sched.Dispose(); progress.Dispose(); defs.Dispose();
        }
    }

    [Fact]
    public void Disposed_scheduler_ignores_ticks()
    {
        var (sched, progress, defs, _) = Build();
        try
        {
            defs.Add(new GandalfTimerDef
            {
                Name = "Quick",
                Duration = TimeSpan.FromMilliseconds(1),
            });
            var id = defs.Definitions[0].Id;
            progress.Start(id);
            System.Threading.Thread.Sleep(20);

            sched.Dispose();

            var expired = new List<TimerExpiredEventArgs>();
            progress.TimerExpired += (_, e) => expired.Add(e);

            sched.TickForTests();

            expired.Should().BeEmpty();
        }
        finally
        {
            progress.Dispose(); defs.Dispose();
        }
    }

    private sealed class ManualTime : TimeProvider
    {
        private DateTimeOffset _now;
        public ManualTime(DateTime utcStart) => _now = new DateTimeOffset(utcStart, TimeSpan.Zero);
        public override DateTimeOffset GetUtcNow() => _now;
        public void Advance(TimeSpan delta) => _now = _now.Add(delta);
    }
}
