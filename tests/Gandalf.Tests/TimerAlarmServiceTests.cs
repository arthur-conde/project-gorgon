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
/// Acceptance tests for <see cref="TimerAlarmService"/> mode-gating.
/// Post-Arda, the replay gate uses <c>_isReplaying</c> tracked via
/// <see cref="CalendarTimeAdvanced"/> events on the
/// <see cref="Arda.Contracts.IDomainEventSubscriber"/> bus.
/// </summary>
[Trait("Category", "FileIO")]
[Collection("FileIO")]
public sealed class TimerAlarmServiceTests : IDisposable
{
    private readonly string _dir;
    private readonly string _defsPath;
    private readonly string _charactersDir;
    private readonly List<IDisposable> _disposables = new();

    public TimerAlarmServiceTests()
    {
        _dir = Mithril.TestSupport.TestPaths.CreateTempDir("gandalf_timer_alarm_mode_gating");
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
    public void OnTimerReady_Replaying_DoesNotPlayAudio()
    {
        var sink = new RecordingAudioSink();
        var bus = new TestDomainEventBus();
        var t0 = new DateTimeOffset(2026, 5, 23, 10, 0, 0, TimeSpan.Zero);
        var cal = new FakeCalendarState { LastTimestamp = t0 };
        var (service, _, _, _) = BuildService(sink, cal, bus);

        // Publish a replay-mode CalendarTimeAdvanced to set _isReplaying = true.
        bus.Publish(new CalendarTimeAdvanced(t0, Meta(t0, isReplay: true)));

        service.OnTimerReady(this, MakeReadyArgs("key1"));

        sink.Plays.Should().BeEmpty(
            "principle 12 — under replay the projection (audio + window flash) is suppressed");
    }

    [Fact]
    public void OnTimerReady_Live_PlaysAudio()
    {
        var sink = new RecordingAudioSink();
        var bus = new TestDomainEventBus();
        var (service, _, _, _) = BuildService(sink, calendarState: null, bus: bus);

        service.OnTimerReady(this, MakeReadyArgs("key1"));

        sink.Plays.Should().HaveCount(1,
            "Live mode is the projection-honest window: side effects fire normally");
        sink.Plays[0].CallerId.Should().Be("gandalf");
    }

    [Fact]
    public void OnTimerReady_NullBus_PlaysAudio()
    {
        var sink = new RecordingAudioSink();
        var (service, _, _, _) = BuildService(sink, calendarState: null, bus: null);

        service.OnTimerReady(this, MakeReadyArgs("key1"));

        sink.Plays.Should().HaveCount(1);
    }

    [Fact]
    public void OnTimerReady_ReplayingThenLive_FiresOnceUnderLive()
    {
        var sink = new RecordingAudioSink();
        var bus = new TestDomainEventBus();
        var t0 = new DateTimeOffset(2026, 5, 23, 10, 0, 0, TimeSpan.Zero);
        var cal = new FakeCalendarState { LastTimestamp = t0 };
        var (service, _, _, _) = BuildService(sink, cal, bus);

        // Replaying — suppressed.
        bus.Publish(new CalendarTimeAdvanced(t0, Meta(t0, isReplay: true)));
        service.OnTimerReady(this, MakeReadyArgs("key1"));
        sink.Plays.Should().BeEmpty();

        // Flip to Live and advance past the 30s refire-suppression window.
        var later = t0 + TimeSpan.FromSeconds(45);
        cal.LastTimestamp = later;
        bus.Publish(new CalendarTimeAdvanced(later, Meta(later)));
        service.OnTimerReady(this, MakeReadyArgs("key1"));

        sink.Plays.Should().HaveCount(1, "Live-mode emission past the suppression window must fire");
    }

    // ── Helpers ─────────────────────────────────────────────────────────────

    private (TimerAlarmService Service, UserTimerSource Source, TimerDefinitionsService Defs, TimerProgressService Progress)
        BuildService(
            IAudioPlaybackSink sink,
            FakeCalendarState? calendarState,
            TestDomainEventBus? bus,
            TimeProvider? time = null)
    {
        var defStore = new JsonSettingsStore<GandalfDefinitions>(_defsPath,
            GandalfDefinitionsJsonContext.Default.GandalfDefinitions);
        var defs = defStore.Load();
        var defsSvc = new TimerDefinitionsService(defStore, defs);
        _disposables.Add(defsSvc);

        var active = new FakeActiveCharacterService();
        var store = new PerCharacterStore<GandalfProgress>(_charactersDir, "gandalf.json",
            GandalfProgressJsonContext.Default.GandalfProgress);
        var view = new PerCharacterView<GandalfProgress>(active, store);
        var progressSvc = new TimerProgressService(view, defsSvc,
            new PerCharacterStoreOptions { CharactersRootDir = _charactersDir });
        _disposables.Add(progressSvc);

        var source = new UserTimerSource(defsSvc, progressSvc);
        _disposables.Add(source);

        var settings = new GandalfSettings
        {
            AlarmEnabled = true,
            FlashWindow = false,
            SoundFilePath = null,
            AlarmVolume = 0.5,
        };

        var service = new TimerAlarmService(source, settings, sink, time,
            calendarState: calendarState, bus: bus);
        _disposables.Add(service);
        return (service, source, defsSvc, progressSvc);
    }

    private static TimerReadyEventArgs MakeReadyArgs(string key) =>
        new()
        {
            SourceId = UserTimerSource.Id,
            Key = key,
            DisplayName = key,
            ReadyAt = DateTimeOffset.UtcNow,
            SourceMetadata = null,
        };
}
