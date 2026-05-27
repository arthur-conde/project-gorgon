using System.IO;
using FluentAssertions;
using Gandalf.Domain;
using Gandalf.Services;
using Mithril.Shared.Character;
using Mithril.Shared.Game;
using Mithril.Shared.Settings;
using Xunit;

namespace Gandalf.Tests;

/// <summary>
/// Regression coverage for the GameTimeOfDay + Recurring path through
/// <see cref="TimerProgressService"/>. The scheduler dispatches off
/// <c>FiringAt</c>; on expiration, recurring rows must:
///   - fire <c>TimerExpired</c> for the just-completed run,
///   - re-arm with a fresh <c>StartedAt</c>/<c>FiringAt</c>,
///   - reset <c>_expiredNotified</c> so the next cycle's fire is allowed.
/// Forgetting that final reset is the silent-second-fire bug the Plan agent
/// flagged as the highest-risk subtask.
/// </summary>
[Trait("Category", "FileIO")]
[Collection("FileIO")]
public class TimerProgressServiceRecurringTests : IDisposable
{
    private readonly string _dir;
    private readonly string _defsPath;
    private readonly string _charactersDir;

    public TimerProgressServiceRecurringTests()
    {
        _dir = Mithril.TestSupport.TestPaths.CreateTempDir("gandalf_progress_recurring");
        _defsPath = Path.Combine(_dir, "definitions.json");
        _charactersDir = Path.Combine(_dir, "characters");
        Directory.CreateDirectory(_charactersDir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { /* best-effort */ }
    }

    private (TimerDefinitionsService defs, TimerProgressService progress, ManualTime time)
        Build(DateTime startUtc)
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

        var time = new ManualTime(startUtc);
        // Use a real GameClock backed by the same TimeProvider so in-game-time
        // arithmetic stays consistent with the test's wall-clock advances.
        var gameClock = new GameClock(time);
        var progressSvc = new TimerProgressService(view, defsSvc,
            new PerCharacterStoreOptions { CharactersRootDir = _charactersDir },
            logger: null, gameClock: gameClock, time: time);
        return (defsSvc, progressSvc, time);
    }

    [Fact]
    public void Countdown_Start_stamps_FiringAt_at_StartedAt_plus_Duration()
    {
        var (defs, progress, time) = Build(new DateTime(2026, 5, 1, 12, 0, 0, DateTimeKind.Utc));
        try
        {
            defs.Add(new GandalfTimerDef { Name = "Soon", Duration = TimeSpan.FromMinutes(15) });
            var id = defs.Definitions[0].Id;
            progress.Start(id);

            var p = progress.GetProgress(id)!;
            p.StartedAt.Should().Be(time.GetUtcNow());
            p.FiringAt.Should().Be(time.GetUtcNow() + TimeSpan.FromMinutes(15));
        }
        finally { progress.Dispose(); defs.Dispose(); }
    }

    [Fact]
    public void GameTimeOfDay_Start_stamps_FiringAt_at_next_in_game_occurrence()
    {
        // Pgemissary anchor: 2026-03-11T01:45:01.212Z = 21:00 in-game. Five real
        // minutes later → 22:00 in-game. Asking for "next 23:00 in-game" should
        // land 5 real minutes further on (= 10 real min after anchor).
        var startUtc = new DateTime(2026, 3, 11, 1, 50, 1, 212, DateTimeKind.Utc);
        var (defs, progress, time) = Build(startUtc);
        try
        {
            defs.Add(new GandalfTimerDef
            {
                Name = "11 PM in-game",
                Kind = GandalfTriggerKind.GameTimeOfDay,
                GameHour = 23, GameMinute = 0,
                Recurring = false,
            });
            var id = defs.Definitions[0].Id;
            progress.Start(id);

            var p = progress.GetProgress(id)!;
            p.FiringAt.Should().NotBeNull();
            (p.FiringAt!.Value - p.StartedAt!.Value)
                .Should().Be(TimeSpan.FromMinutes(5));
        }
        finally { progress.Dispose(); defs.Dispose(); }
    }

    [Fact]
    public void GameTimeOfDay_one_shot_fires_once_then_latches_Done()
    {
        var startUtc = new DateTime(2026, 3, 11, 1, 50, 1, 212, DateTimeKind.Utc);
        var (defs, progress, time) = Build(startUtc);
        var fires = 0;
        progress.TimerExpired += (_, _) => fires++;
        try
        {
            defs.Add(new GandalfTimerDef
            {
                Name = "11 PM (one-shot)",
                Kind = GandalfTriggerKind.GameTimeOfDay,
                GameHour = 23, GameMinute = 0,
                Recurring = false,
            });
            var id = defs.Definitions[0].Id;
            progress.Start(id);

            time.Advance(TimeSpan.FromMinutes(5) + TimeSpan.FromSeconds(1));
            progress.CheckExpirations();
            fires.Should().Be(1);
            progress.GetProgress(id)!.CompletedAt.Should().NotBeNull("one-shot latches Done");

            // Subsequent checks must not re-fire.
            time.Advance(TimeSpan.FromHours(3));
            progress.CheckExpirations();
            fires.Should().Be(1);
        }
        finally { progress.Dispose(); defs.Dispose(); }
    }

    [Fact]
    public void GameTimeOfDay_recurring_fires_each_cycle_and_re_arms()
    {
        // The bug this guards against: forgetting to reset _expiredNotified
        // (or _firedKeys in the alarm service) silently swallows the second
        // fire. Drive two consecutive cycles and assert two fires.
        var startUtc = new DateTime(2026, 3, 11, 1, 50, 1, 212, DateTimeKind.Utc);
        var (defs, progress, time) = Build(startUtc);
        var fires = 0;
        progress.TimerExpired += (_, _) => fires++;
        try
        {
            defs.Add(new GandalfTimerDef
            {
                Name = "11 PM (recurring)",
                Kind = GandalfTriggerKind.GameTimeOfDay,
                GameHour = 23, GameMinute = 0,
                Recurring = true,
            });
            var id = defs.Definitions[0].Id;
            progress.Start(id);

            // Cycle 1.
            time.Advance(TimeSpan.FromMinutes(5) + TimeSpan.FromSeconds(1));
            progress.CheckExpirations();
            fires.Should().Be(1, "first cycle should have fired");
            var p = progress.GetProgress(id)!;
            p.CompletedAt.Should().BeNull("recurring re-arms instead of latching Done");
            p.StartedAt.Should().Be(time.GetUtcNow(), "recurring StartedAt anchors at re-arm wall-clock");
            p.FiringAt.Should().NotBeNull();
            // Next FiringAt should be one full real cycle (~7200 s) ahead. The
            // exact offset depends on where in the cycle we landed, but it
            // must be at least an hour out.
            (p.FiringAt!.Value - time.GetUtcNow()).Should().BeGreaterThan(TimeSpan.FromHours(1));

            // Cycle 2 — advance to just past the second FiringAt.
            time.Advance(p.FiringAt.Value - time.GetUtcNow() + TimeSpan.FromSeconds(1));
            progress.CheckExpirations();
            fires.Should().Be(2, "the second cycle must fire — regression on the silent-second-fire bug");
            progress.GetProgress(id)!.CompletedAt.Should().BeNull();
        }
        finally { progress.Dispose(); defs.Dispose(); }
    }

    private sealed class ManualTime : TimeProvider
    {
        private DateTimeOffset _now;
        public ManualTime(DateTime utcStart) => _now = new DateTimeOffset(utcStart, TimeSpan.Zero);
        public override DateTimeOffset GetUtcNow() => _now;
        public void Advance(TimeSpan delta) => _now = _now.Add(delta);
    }
}
