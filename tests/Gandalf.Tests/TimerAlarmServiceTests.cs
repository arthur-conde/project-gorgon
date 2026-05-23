using System.IO;
using FluentAssertions;
using Gandalf.Domain;
using Gandalf.Services;
using Mithril.Shared.Audio;
using Mithril.Shared.Character;
using Mithril.Shared.Settings;
using Mithril.WorldSim;
using Mithril.WorldSim.Player;
using Xunit;

namespace Gandalf.Tests;

/// <summary>
/// Call 3 / principle 12 acceptance tests for <see cref="TimerAlarmService"/>.
///
/// <para>Under <see cref="WorldMode.Replaying"/>, <c>OnTimerReady</c> must NOT
/// fire user-facing side effects (audio playback, window flash). Upstream state
/// derivation (the <c>_firedAt</c> dedup ledger) stays mode-agnostic so a
/// transition to <see cref="WorldMode.Live"/> mid-session doesn't re-blast
/// alarms the user already lived through.</para>
///
/// <para>The original sink-list source is the Call 3 ratification in
/// <c>docs/world-simulator.md</c> §Decisions ratified post-#642 (resolves #676).</para>
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

    [Fact]
    public void OnTimerReady_Replaying_DoesNotPlayAudio()
    {
        var sink = new RecordingAudioSink();
        var world = new TestPlayerWorld { Clock = { Mode = WorldMode.Replaying } };
        var (service, _, _, _) = BuildService(sink, world);

        service.OnTimerReady(this, MakeReadyArgs("key1"));

        sink.Plays.Should().BeEmpty(
            "principle 12 — under WorldMode.Replaying the projection (audio + window flash) is suppressed; "
            + "the user already lived through these events in real time");
    }

    [Fact]
    public void OnTimerReady_Live_PlaysAudio()
    {
        var sink = new RecordingAudioSink();
        var world = new TestPlayerWorld { Clock = { Mode = WorldMode.Live } };
        var (service, _, _, _) = BuildService(sink, world);

        service.OnTimerReady(this, MakeReadyArgs("key1"));

        sink.Plays.Should().HaveCount(1,
            "Live mode is the projection-honest window: side effects fire normally");
        sink.Plays[0].CallerId.Should().Be("gandalf");
    }

    [Fact]
    public void OnTimerReady_NullPlayerWorld_PlaysAudio()
    {
        // Defensive default: when no IPlayerWorld is injected (e.g., a
        // partial-composition test or pre-#601 code path), the
        // _worldClock?.Mode null-conditional treats the world as Live so
        // existing call sites aren't broken by the guard.
        var sink = new RecordingAudioSink();
        var (service, _, _, _) = BuildService(sink, playerWorld: null);

        service.OnTimerReady(this, MakeReadyArgs("key1"));

        sink.Plays.Should().HaveCount(1);
    }

    [Fact]
    public void OnTimerReady_ReplayingThenLive_FiresOnceUnderLive()
    {
        // Boundary transition shape: the same key fires once during replay
        // drain (suppressed by the guard) and once during Live (normal
        // fire). The _firedAt write happens before the guard so the
        // refire-suppression window is honoured across the boundary;
        // here the Live emission lands well past the 30s window so it
        // fires cleanly.
        var sink = new RecordingAudioSink();
        var world = new TestPlayerWorld
        {
            Clock = { Mode = WorldMode.Replaying, Now = new DateTimeOffset(2026, 5, 23, 10, 0, 0, TimeSpan.Zero) },
        };
        var (service, _, _, _) = BuildService(sink, world);

        // Replaying — suppressed.
        service.OnTimerReady(this, MakeReadyArgs("key1"));
        sink.Plays.Should().BeEmpty();

        // Flip to Live and advance the world clock past the 30s
        // refire-suppression window (the SUT reads _worldClock?.Now over
        // the TimeProvider fallback).
        world.Clock.Mode = WorldMode.Live;
        world.Clock.Now += TimeSpan.FromSeconds(45);
        service.OnTimerReady(this, MakeReadyArgs("key1"));

        sink.Plays.Should().HaveCount(1, "Live-mode emission past the suppression window must fire");
    }

    // ── Helpers ─────────────────────────────────────────────────────────────

    private (TimerAlarmService Service, UserTimerSource Source, TimerDefinitionsService Defs, TimerProgressService Progress)
        BuildService(
            IAudioPlaybackSink sink,
            IPlayerWorld? playerWorld,
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
            FlashWindow = false,    // no Application.MainWindow available — keep the assertion focused on audio.
            SoundFilePath = null,   // AudioPlayer falls back to beep on null; the fake sink records the call regardless.
            AlarmVolume = 0.5,
        };

        var service = new TimerAlarmService(source, settings, sink, time, playerWorld);
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
