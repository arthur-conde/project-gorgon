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
        var world = new FakePlayerWorld(WorldMode.Replaying);
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
        var world = new FakePlayerWorld(WorldMode.Live);
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
        var world = new FakePlayerWorld(WorldMode.Replaying);
        // Seed the world clock to a stable instant so the SUT's `Now`
        // reads (which prefer _worldClock?.Now over the time provider)
        // see a non-MinValue value across both calls.
        world.WorldClock.Now = new DateTimeOffset(2026, 5, 23, 10, 0, 0, TimeSpan.Zero);
        var (service, _, _, _) = BuildService(sink, world);

        // Replaying — suppressed.
        service.OnTimerReady(this, MakeReadyArgs("key1"));
        sink.Plays.Should().BeEmpty();

        // Flip to Live and advance the world clock past the 30s
        // refire-suppression window (the SUT reads _worldClock?.Now over
        // the TimeProvider fallback).
        world.WorldClock.Mode = WorldMode.Live;
        world.WorldClock.Now += TimeSpan.FromSeconds(45);
        service.OnTimerReady(this, MakeReadyArgs("key1"));

        sink.Plays.Should().HaveCount(1, "Live-mode emission past the suppression window must fire");
    }

    // ── Helpers ─────────────────────────────────────────────────────────────

    private (TimerAlarmService Service, UserTimerSource Source, TimerDefinitionsService Defs, TimerProgressService Progress)
        BuildService(
            IAudioPlaybackSink sink,
            FakePlayerWorld? playerWorld,
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

    /// <summary>
    /// Recording <see cref="IAudioPlaybackSink"/> for Mode-gating verification.
    /// Local to this fixture because Gandalf.Tests doesn't share Samwise's
    /// <c>FakeAudioPlaybackSink</c>.
    /// </summary>
    private sealed class RecordingAudioSink : IAudioPlaybackSink
    {
        public sealed record PlayCall(string? Path, float Volume, string? CallerId, bool Loop);

        public List<PlayCall> Plays { get; } = new();
        public int GlobalStopCount { get; private set; }
        public List<string> CallerStops { get; } = new();

        public IPlaybackHandle Play(string? path, float volume = 1.0f, string? callerId = null, bool loop = false)
        {
            Plays.Add(new PlayCall(path, volume, callerId, loop));
            return EmptyHandle.Instance;
        }

        public void Stop() => GlobalStopCount++;
        public void Stop(string callerId) => CallerStops.Add(callerId);

        private sealed class EmptyHandle : IPlaybackHandle
        {
            public static readonly EmptyHandle Instance = new();
            public bool IsPlaying => false;
            public void Stop() { }
            public void Dispose() { }
        }
    }

    /// <summary>
    /// Minimal mutable <see cref="TimeProvider"/> for boundary-transition
    /// tests. The production refire-suppression check uses the world clock
    /// when one is injected and this fallback when not; here both reads
    /// happen against the world clock since we inject one, but the SUT
    /// still asks the provider for absolute Now in places, so we hand it
    /// a controllable provider.
    /// </summary>
    private sealed class RewindableTimeProvider : TimeProvider
    {
        public DateTimeOffset Now { get; private set; }
        public RewindableTimeProvider(DateTime utc) => Now = new DateTimeOffset(utc, TimeSpan.Zero);
        public override DateTimeOffset GetUtcNow() => Now;
        public void Advance(TimeSpan ts) => Now += ts;
    }

    /// <summary>
    /// Local <see cref="IPlayerWorld"/> stub — Gandalf.Tests doesn't share
    /// Samwise's <c>FakePlayerWorld</c>. Only <see cref="IWorldClock.Mode"/>
    /// is read by the SUT; the bus/folder surfaces throw to flag accidental
    /// use.
    /// </summary>
    private sealed class FakePlayerWorld : IPlayerWorld
    {
        public FakePlayerWorld(WorldMode mode) => WorldClock = new() { Mode = mode };
        public FakeWorldClock WorldClock { get; }
        public IWorldClock Clock => WorldClock;
        public IWorldEventBus Bus => throw new NotSupportedException(
            "FakePlayerWorld exposes only Clock; bus/folder/composer surfaces aren't needed for Mode-gating tests.");
        public void RegisterProducer<T>(IFrameProducer<T> producer) =>
            throw new NotSupportedException("FakePlayerWorld is read-only.");
        public void RegisterFolder<T>(IFolder<T> folder) =>
            throw new NotSupportedException("FakePlayerWorld is read-only.");
        public void RegisterComposer(IComposer composer) =>
            throw new NotSupportedException("FakePlayerWorld is read-only.");
        public Task StartMerger(CancellationToken ct) => Task.CompletedTask;
    }

    private sealed class FakeWorldClock : IWorldClock
    {
        public DateTimeOffset Now { get; set; } = DateTimeOffset.MinValue;
        public long Frame { get; set; }
        public WorldMode Mode { get; set; } = WorldMode.Live;
    }
}
