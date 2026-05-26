using System.IO;
using Arda.Abstractions.Logs;
using Arda.World.Player.Events;
using FluentAssertions;
using Gandalf.Domain;
using Gandalf.Services;
using Mithril.Shared.Audio;
using Mithril.Shared.Character;
using Mithril.Shared.Settings;
using Xunit;

namespace Gandalf.Tests;

/// <summary>
/// Acceptance coverage for <see cref="TimerExpirationDriver"/> (Arda migration).
/// The driver subscribes to <see cref="CalendarTimeAdvanced"/> domain events via
/// <see cref="Arda.Contracts.IDomainEventSubscriber"/> and forwards each event's
/// <see cref="CalendarTimeAdvanced.Now"/> into
/// <see cref="TimerProgressService.CheckExpirations(DateTimeOffset)"/>.
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

    private static LogLineMetadata Meta(DateTimeOffset at, bool isReplay = false) =>
        new(at, DateTimeOffset.UtcNow, isReplay);

    [Fact]
    public async Task CalendarTimeAdvanced_drives_CheckExpirations()
    {
        var t0 = new DateTimeOffset(2026, 5, 23, 12, 0, 0, TimeSpan.Zero);
        var (defs, progress, bus) = Build(t0);

        defs.Add(new GandalfTimerDef { Name = "Soon", Duration = TimeSpan.FromSeconds(30) });
        var id = defs.Definitions[0].Id;
        progress.Start(id);

        var driver = new TimerExpirationDriver(progress, bus);
        _disposables.Add(driver);
        await driver.StartAsync(CancellationToken.None);

        var tickAt = t0 + TimeSpan.FromSeconds(31);
        bus.Publish(new CalendarTimeAdvanced(tickAt, Meta(tickAt)));

        progress.GetProgress(id)!.CompletedAt.Should().NotBeNull(
            "CalendarTimeAdvanced drives CheckExpirations with the event's Now");
    }

    [Fact]
    public async Task Missed_alarms_during_Replaying_drain_stamp_silently_then_no_retroactive_audio()
    {
        var t0 = new DateTimeOffset(2026, 5, 23, 12, 0, 0, TimeSpan.Zero);
        var sink = new RecordingAudioSink();
        var (defs, progress, bus) = Build(t0);
        var cal = new FakeCalendarState { LastTimestamp = t0 };

        defs.Add(new GandalfTimerDef { Name = "Past", Duration = TimeSpan.FromSeconds(30) });
        var pastId = defs.Definitions[0].Id;
        progress.Start(pastId);

        var source = new UserTimerSource(defs, progress);
        _disposables.Add(source);
        var settings = new GandalfSettings { AlarmEnabled = true, FlashWindow = false };
        var alarm = new TimerAlarmService(source, settings, sink, time: null,
            calendarState: cal, bus: bus);
        _disposables.Add(alarm);

        var driver = new TimerExpirationDriver(progress, bus);
        _disposables.Add(driver);
        await driver.StartAsync(CancellationToken.None);

        // Drain-time calendar tick — crosses the timer's FiringAt while IsReplay=true.
        var drainTickAt = t0 + TimeSpan.FromSeconds(31);
        cal.LastTimestamp = drainTickAt;
        bus.Publish(new CalendarTimeAdvanced(drainTickAt, Meta(drainTickAt, isReplay: true)));

        progress.GetProgress(pastId)!.CompletedAt.Should().NotBeNull(
            "drain-time replay must still advance state");
        sink.Plays.Should().BeEmpty(
            "drain-time alarms update state but do not play audio");

        // Mode flips to Live. No retroactive audio.
        var liveTickAt = drainTickAt + TimeSpan.FromSeconds(1);
        cal.LastTimestamp = liveTickAt;
        bus.Publish(new CalendarTimeAdvanced(liveTickAt, Meta(liveTickAt)));

        sink.Plays.Should().BeEmpty(
            "the Live transition does not re-fire a timer that already stamped CompletedAt in Replaying");

        // A FRESH timer armed post-flip fires audibly.
        defs.Add(new GandalfTimerDef { Name = "Fresh", Duration = TimeSpan.FromSeconds(5) });
        var freshId = defs.Definitions[1].Id;
        var freshArmedAt = liveTickAt + TimeSpan.FromMinutes(1);
        cal.LastTimestamp = freshArmedAt;
        progress.Start(freshId);

        var freshFireAt = freshArmedAt + TimeSpan.FromSeconds(6);
        cal.LastTimestamp = freshFireAt;
        bus.Publish(new CalendarTimeAdvanced(freshFireAt, Meta(freshFireAt)));

        sink.Plays.Should().HaveCount(1, "fresh post-flip timer fires audibly on its calendar tick");

        await driver.StopAsync(CancellationToken.None);
    }

    [Fact]
    public async Task Disposed_driver_unsubscribes()
    {
        var t0 = new DateTimeOffset(2026, 5, 23, 12, 0, 0, TimeSpan.Zero);
        var (defs, progress, bus) = Build(t0);
        defs.Add(new GandalfTimerDef { Name = "X", Duration = TimeSpan.FromSeconds(30) });
        var id = defs.Definitions[0].Id;
        progress.Start(id);

        var driver = new TimerExpirationDriver(progress, bus);
        await driver.StartAsync(CancellationToken.None);
        await driver.StopAsync(CancellationToken.None);
        driver.Dispose();

        var tickAt = t0 + TimeSpan.FromMinutes(1);
        bus.Publish(new CalendarTimeAdvanced(tickAt, Meta(tickAt)));

        progress.GetProgress(id)!.CompletedAt.Should().BeNull(
            "the subscription should be disposed on host stop / driver dispose");
    }

    // ── Helpers ─────────────────────────────────────────────────────────────

    private (TimerDefinitionsService Defs, TimerProgressService Progress, TestDomainEventBus Bus)
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

        var bus = new TestDomainEventBus();
        return (defsSvc, progressSvc, bus);
    }

    private sealed class ManualTime : TimeProvider
    {
        private DateTimeOffset _now;
        public ManualTime(DateTimeOffset start) => _now = start;
        public override DateTimeOffset GetUtcNow() => _now;
        public void Advance(TimeSpan delta) => _now = _now.Add(delta);
    }
}
