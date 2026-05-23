using System.IO;
using FluentAssertions;
using Gandalf.Domain;
using Gandalf.Services;
using Mithril.Shared.Audio;
using Mithril.Shared.Character;
using Mithril.Shared.Settings;
using Mithril.WorldSim;
using Xunit;

namespace Gandalf.Tests;

/// <summary>
/// Acceptance coverage for the scheduler-collapse driver (#613). The
/// <see cref="TimerExpirationDriver"/> subscribes to PlayerWorld's
/// <see cref="CalendarTimeAdvanced"/> events on
/// <see cref="Microsoft.Extensions.Hosting.IHostedService.StartAsync"/>,
/// then forwards each event's <see cref="CalendarTimeAdvanced.Now"/> into
/// <see cref="TimerProgressService.CheckExpirations(DateTimeOffset)"/>.
///
/// <para>The missed-alarms-on-restart case is the central assertion:
/// timers that expired during the replay drain advance to expired-but-
/// silent (CompletedAt stamped; <see cref="TimerExpiredEventArgs"/> still
/// fires for downstream consumers, but the alarm service's Mode gate
/// suppresses audio). The Mode flip does NOT retroactively re-fire an
/// already-stamped timer.</para>
/// </summary>
[Trait("Category", "FileIO")]
[Collection("FileIO")]
public sealed class TimerExpirationDriverTests : IDisposable
{
    private readonly string _dir;
    private readonly string _defsPath;
    private readonly string _charactersDir;
    private readonly List<IDisposable> _disposables = new();

    public TimerExpirationDriverTests()
    {
        _dir = Mithril.TestSupport.TestPaths.CreateTempDir("gandalf_expiration_driver");
        _defsPath = Path.Combine(_dir, "definitions.json");
        _charactersDir = Path.Combine(_dir, "characters");
        Directory.CreateDirectory(_charactersDir);
    }

    public void Dispose()
    {
        for (int i = _disposables.Count - 1; i >= 0; i--)
        {
            try { _disposables[i].Dispose(); } catch { /* best-effort */ }
        }
        try { Directory.Delete(_dir, recursive: true); } catch { /* best-effort */ }
    }

    [Fact]
    public async Task CalendarTimeAdvanced_drives_CheckExpirations()
    {
        // Smoke: arm a 30s countdown at T0, publish a calendar tick at
        // T0 + 31s, assert the timer stamps CompletedAt.
        var t0 = new DateTimeOffset(2026, 5, 23, 12, 0, 0, TimeSpan.Zero);
        var (defs, progress, world) = Build(t0);

        defs.Add(new GandalfTimerDef { Name = "Soon", Duration = TimeSpan.FromSeconds(30) });
        var id = defs.Definitions[0].Id;
        progress.Start(id);

        var driver = new TimerExpirationDriver(progress, world);
        _disposables.Add(driver);
        await driver.StartAsync(CancellationToken.None);

        world.TestBus.Publish(t0 + TimeSpan.FromSeconds(31),
            new CalendarTimeAdvanced(t0 + TimeSpan.FromSeconds(31), WorldMode.Live));

        progress.GetProgress(id)!.CompletedAt.Should().NotBeNull(
            "CalendarTimeAdvanced drives CheckExpirations with the event's Now");
    }

    [Fact]
    public async Task Missed_alarms_during_Replaying_drain_stamp_silently_then_no_retroactive_audio()
    {
        // Acceptance bullet from issue #613 — missed-alarms-on-restart.
        //
        // Setup: a 30s countdown armed at T0 with a wall-clock "Now" already
        // past its FiringAt. The world drains in Replaying mode and a single
        // CalendarTimeAdvanced tick crosses the FiringAt boundary.
        //
        // Expected:
        //   - CheckExpirations stamps CompletedAt (state advances)
        //   - TimerExpired fires downstream — the alarm service receives the
        //     event but its Mode-gate (TimerAlarmService.OnTimerReady, line
        //     "if (_worldClock?.Mode == WorldMode.Replaying) return;")
        //     suppresses audio
        //   - Mode flip to Live does NOT re-fire the already-stamped timer
        //   - A fresh timer armed post-flip fires audibly on its tick
        var t0 = new DateTimeOffset(2026, 5, 23, 12, 0, 0, TimeSpan.Zero);
        var sink = new RecordingAudioSink();
        var (defs, progress, world) = Build(t0);
        // World boots Replaying — drain in progress.
        world.Clock.Mode = WorldMode.Replaying;
        world.Clock.Now = t0;

        // Arm a timer that should already be expired at the first drain tick.
        defs.Add(new GandalfTimerDef { Name = "Past", Duration = TimeSpan.FromSeconds(30) });
        var pastId = defs.Definitions[0].Id;
        progress.Start(pastId);

        // Wire the alarm path so the Mode gate is the assertion under test.
        var source = new UserTimerSource(defs, progress);
        _disposables.Add(source);
        var settings = new GandalfSettings { AlarmEnabled = true, FlashWindow = false };
        var alarm = new TimerAlarmService(source, settings, sink, time: null, playerWorld: world);
        _disposables.Add(alarm);

        var driver = new TimerExpirationDriver(progress, world);
        _disposables.Add(driver);
        await driver.StartAsync(CancellationToken.None);

        // Drain-time calendar tick — crosses the timer's FiringAt while
        // Mode == Replaying. The state should advance, audio should NOT play.
        var drainTickAt = t0 + TimeSpan.FromSeconds(31);
        world.Clock.Now = drainTickAt;
        world.TestBus.Publish(drainTickAt, new CalendarTimeAdvanced(drainTickAt, WorldMode.Replaying));

        progress.GetProgress(pastId)!.CompletedAt.Should().NotBeNull(
            "drain-time replay must still advance state");
        sink.Plays.Should().BeEmpty(
            "drain-time alarms update state but do not play audio (issue #613 acceptance bullet)");

        // Mode flips to Live. No retroactive audio for the already-expired timer.
        world.Clock.Mode = WorldMode.Live;
        var liveTickAt = drainTickAt + TimeSpan.FromSeconds(1);
        world.Clock.Now = liveTickAt;
        world.TestBus.Publish(liveTickAt, new CalendarTimeAdvanced(liveTickAt, WorldMode.Live));

        sink.Plays.Should().BeEmpty(
            "the Live transition does not re-fire a timer that already stamped CompletedAt in Replaying");

        // A FRESH timer armed post-flip — its tick fires audibly. Past the
        // 30-second refire-suppression window the alarm service uses, so the
        // dedup ledger doesn't collide.
        defs.Add(new GandalfTimerDef { Name = "Fresh", Duration = TimeSpan.FromSeconds(5) });
        var freshId = defs.Definitions[1].Id;
        // Advance the world clock past the refire-suppression window so the
        // fresh timer's key (different from the past timer's key anyway)
        // doesn't risk collision.
        var freshArmedAt = liveTickAt + TimeSpan.FromMinutes(1);
        world.Clock.Now = freshArmedAt;
        progress.Start(freshId);

        var freshFireAt = freshArmedAt + TimeSpan.FromSeconds(6);
        world.Clock.Now = freshFireAt;
        world.TestBus.Publish(freshFireAt, new CalendarTimeAdvanced(freshFireAt, WorldMode.Live));

        sink.Plays.Should().HaveCount(1, "fresh post-flip timer fires audibly on its calendar tick");

        await driver.StopAsync(CancellationToken.None);
    }

    [Fact]
    public async Task Disposed_driver_unsubscribes()
    {
        var t0 = new DateTimeOffset(2026, 5, 23, 12, 0, 0, TimeSpan.Zero);
        var (defs, progress, world) = Build(t0);
        defs.Add(new GandalfTimerDef { Name = "X", Duration = TimeSpan.FromSeconds(30) });
        var id = defs.Definitions[0].Id;
        progress.Start(id);

        var driver = new TimerExpirationDriver(progress, world);
        await driver.StartAsync(CancellationToken.None);
        await driver.StopAsync(CancellationToken.None);
        driver.Dispose();

        // Publishing after dispose should not advance progress.
        world.TestBus.Publish(t0 + TimeSpan.FromMinutes(1),
            new CalendarTimeAdvanced(t0 + TimeSpan.FromMinutes(1), WorldMode.Live));

        progress.GetProgress(id)!.CompletedAt.Should().BeNull(
            "the subscription should be disposed on host stop / driver dispose");
    }

    // ── Helpers ─────────────────────────────────────────────────────────────

    private (TimerDefinitionsService Defs, TimerProgressService Progress, TestPlayerWorld World)
        Build(DateTimeOffset t0)
    {
        var defStore = new JsonSettingsStore<GandalfDefinitions>(_defsPath,
            GandalfDefinitionsJsonContext.Default.GandalfDefinitions);
        var defs = defStore.Load();
        var defsSvc = new TimerDefinitionsService(defStore, defs);
        _disposables.Add(defsSvc);

        var active = new FakeActiveCharacterService();
        active.SetActiveCharacter("Arthur", "Kwatoxi");
        var store = new PerCharacterStore<GandalfProgress>(_charactersDir, "gandalf.json",
            GandalfProgressJsonContext.Default.GandalfProgress);
        var view = new PerCharacterView<GandalfProgress>(active, store);
        var time = new ManualTime(t0);
        var progressSvc = new TimerProgressService(view, defsSvc,
            new PerCharacterStoreOptions { CharactersRootDir = _charactersDir },
            diag: null, gameClock: null, time: time);
        _disposables.Add(progressSvc);

        var world = new TestPlayerWorld { Clock = { Now = t0, Mode = WorldMode.Live } };
        return (defsSvc, progressSvc, world);
    }

    private sealed class ManualTime : TimeProvider
    {
        private DateTimeOffset _now;
        public ManualTime(DateTimeOffset start) => _now = start;
        public override DateTimeOffset GetUtcNow() => _now;
        public void Advance(TimeSpan delta) => _now = _now.Add(delta);
    }
}
