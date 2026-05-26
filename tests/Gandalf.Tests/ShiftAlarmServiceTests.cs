using Arda.Abstractions.Logs;
using Arda.World.Player.Events;
using FluentAssertions;
using Gandalf.Domain;
using Gandalf.Services;
using Mithril.Shared.Audio;
using Mithril.Shared.Game;
using Xunit;

namespace Gandalf.Tests;

/// <summary>
/// Coverage for <see cref="ShiftAlarmService"/> (Arda migration).
/// The service subscribes to <see cref="TimeOfDayShifted"/> domain events
/// via <see cref="Arda.Dispatch.IDomainEventSubscriber"/>; the test
/// fixture pushes synthetic shifts through the test hook directly.
/// </summary>
public class ShiftAlarmServiceTests
{
    private static readonly IShiftCatalog Catalog = new JsonShiftCatalog();

    private static LogLineMetadata Meta(DateTimeOffset at, bool isReplay = false) =>
        new(at, DateTimeOffset.UtcNow, isReplay);

    [Fact]
    public void ShouldAlarm_requires_both_global_enabled_and_per_shift_enabled()
    {
        var on = new GandalfSettings { AlarmEnabled = true };
        var off = new GandalfSettings { AlarmEnabled = false };
        var enabled = new ShiftAlarmConfig { Enabled = true };
        var disabled = new ShiftAlarmConfig { Enabled = false };

        ShiftAlarmService.ShouldAlarm(enabled, on).Should().BeTrue();
        ShiftAlarmService.ShouldAlarm(disabled, on).Should().BeFalse("per-shift toggle is off");
        ShiftAlarmService.ShouldAlarm(enabled, off).Should().BeFalse("global alarms are off");
        ShiftAlarmService.ShouldAlarm(disabled, off).Should().BeFalse();
    }

    [Fact]
    public void ResolveSoundPath_prefers_per_shift_over_global()
    {
        var global = new GandalfSettings { SoundFilePath = @"C:\global\default.wav" };
        var withOverride = new ShiftAlarmConfig { SoundFilePath = @"C:\custom\dawn.wav" };
        ShiftAlarmService.ResolveSoundPath(withOverride, global).Should().Be(@"C:\custom\dawn.wav");
    }

    [Fact]
    public void ResolveSoundPath_falls_back_to_global_when_per_shift_is_null()
    {
        var global = new GandalfSettings { SoundFilePath = @"C:\global\default.wav" };
        var bare = new ShiftAlarmConfig { SoundFilePath = null };
        ShiftAlarmService.ResolveSoundPath(bare, global).Should().Be(@"C:\global\default.wav");
    }

    [Fact]
    public void ResolveSoundPath_returns_null_when_neither_is_set()
    {
        ShiftAlarmService.ResolveSoundPath(new ShiftAlarmConfig(), new GandalfSettings()).Should().BeNull();
    }

    [Fact]
    public void GetOrCreate_returns_a_disabled_default_for_an_untouched_shift()
    {
        var shifts = new GandalfShiftSettings();
        foreach (var s in Catalog.Shifts)
        {
            var c = shifts.GetOrCreate(s.Slug);
            c.Enabled.Should().BeFalse();
            c.SoundFilePath.Should().BeNull();
        }
    }

    [Fact]
    public void Settings_keyed_by_pre_refactor_slug_survive_catalog_swap()
    {
        var preRefactor = new GandalfShiftSettings();
        preRefactor.GetOrCreate("dawn").Enabled = true;
        preRefactor.GetOrCreate("night").Enabled = true;
        preRefactor.GetOrCreate("dusk").SoundFilePath = @"C:\sounds\dusk.wav";

        var liveDawn = preRefactor.GetOrCreate("dawn");
        var liveNight = preRefactor.GetOrCreate("night");
        var liveDusk = preRefactor.GetOrCreate("dusk");

        liveDawn.Enabled.Should().BeTrue("the toggle survives across the refactor");
        liveNight.Enabled.Should().BeTrue();
        liveDusk.SoundFilePath.Should().Be(@"C:\sounds\dusk.wav");

        foreach (var s in Catalog.Shifts)
        {
            var rehydrated = preRefactor.GetOrCreate(s.Slug);
            rehydrated.Should().NotBeNull();
        }
        preRefactor.GetOrCreate("dawn").Enabled.Should().BeTrue();
        preRefactor.GetOrCreate("night").Enabled.Should().BeTrue();
    }

    [Fact]
    public void Live_TimeOfDayShifted_for_enabled_shift_plays_audio()
    {
        var sink = new RecordingAudioSink();
        var settings = new GandalfSettings { AlarmEnabled = true };
        var shifts = new GandalfShiftSettings();
        shifts.GetOrCreate("dawn").Enabled = true;
        var bus = new TestDomainEventBus();
        var at = new DateTimeOffset(2026, 5, 23, 5, 0, 0, TimeSpan.Zero);
        using var svc = new ShiftAlarmService(Catalog, settings, shifts, sink, bus);

        svc.OnShiftTransitionForTests(new TimeOfDayShifted(From: "night", To: "dawn",
            At: at, Meta(at)));

        sink.Plays.Should().HaveCount(1, "Live-mode shift transition for an enabled shift fires audio");
        sink.Plays[0].CallerId.Should().Be("gandalf");
        svc.LastObservedShift.Should().Be("dawn");
    }

    [Fact]
    public void Replaying_TimeOfDayShifted_advances_state_but_does_not_play_audio()
    {
        var sink = new RecordingAudioSink();
        var settings = new GandalfSettings { AlarmEnabled = true };
        var shifts = new GandalfShiftSettings();
        shifts.GetOrCreate("dawn").Enabled = true;
        var bus = new TestDomainEventBus();
        using var svc = new ShiftAlarmService(Catalog, settings, shifts, sink, bus);

        svc.OnShiftTransitionForTests(new TimeOfDayShifted(null, "dawn",
            new DateTimeOffset(2026, 5, 23, 5, 0, 0, TimeSpan.Zero),
            Meta(new DateTimeOffset(2026, 5, 23, 5, 0, 0, TimeSpan.Zero), isReplay: true)));

        sink.Plays.Should().BeEmpty(
            "principle 12 — Replaying-mode side effects (audio + window flash) are suppressed");
        svc.LastObservedShift.Should().Be("dawn",
            "state derivation is mode-agnostic so the next Live tick reuses a coherent suppression contract");
    }

    [Fact]
    public void ReplayingThenLive_same_shift_does_not_retroactively_fire()
    {
        var sink = new RecordingAudioSink();
        var settings = new GandalfSettings { AlarmEnabled = true };
        var shifts = new GandalfShiftSettings();
        shifts.GetOrCreate("dawn").Enabled = true;
        var bus = new TestDomainEventBus();
        using var svc = new ShiftAlarmService(Catalog, settings, shifts, sink, bus);

        svc.OnShiftTransitionForTests(new TimeOfDayShifted(null, "dawn",
            new DateTimeOffset(2026, 5, 23, 5, 0, 0, TimeSpan.Zero),
            Meta(new DateTimeOffset(2026, 5, 23, 5, 0, 0, TimeSpan.Zero), isReplay: true)));
        sink.Plays.Should().BeEmpty();

        svc.OnShiftTransitionForTests(new TimeOfDayShifted("night", "dawn",
            new DateTimeOffset(2026, 5, 23, 5, 0, 1, TimeSpan.Zero),
            Meta(new DateTimeOffset(2026, 5, 23, 5, 0, 1, TimeSpan.Zero))));
        sink.Plays.Should().BeEmpty("dedup ledger blocks retroactive ring for an already-observed shift");
    }

    [Fact]
    public void Live_NewShift_after_Replaying_drain_fires_normally()
    {
        var sink = new RecordingAudioSink();
        var settings = new GandalfSettings { AlarmEnabled = true };
        var shifts = new GandalfShiftSettings();
        shifts.GetOrCreate("dawn").Enabled = true;
        shifts.GetOrCreate("morning").Enabled = true;
        var bus = new TestDomainEventBus();
        using var svc = new ShiftAlarmService(Catalog, settings, shifts, sink, bus);

        svc.OnShiftTransitionForTests(new TimeOfDayShifted(null, "dawn",
            new DateTimeOffset(2026, 5, 23, 5, 0, 0, TimeSpan.Zero),
            Meta(new DateTimeOffset(2026, 5, 23, 5, 0, 0, TimeSpan.Zero), isReplay: true)));
        sink.Plays.Should().BeEmpty();

        svc.OnShiftTransitionForTests(new TimeOfDayShifted("dawn", "morning",
            new DateTimeOffset(2026, 5, 23, 8, 0, 0, TimeSpan.Zero),
            Meta(new DateTimeOffset(2026, 5, 23, 8, 0, 0, TimeSpan.Zero))));

        sink.Plays.Should().HaveCount(1, "fresh post-flip transitions fire normally");
        svc.LastObservedShift.Should().Be("morning");
    }

    [Fact]
    public void TimeOfDayShifted_for_disabled_shift_advances_ledger_but_does_not_play()
    {
        var sink = new RecordingAudioSink();
        var settings = new GandalfSettings { AlarmEnabled = true };
        var shifts = new GandalfShiftSettings();
        var bus = new TestDomainEventBus();
        using var svc = new ShiftAlarmService(Catalog, settings, shifts, sink, bus);

        svc.OnShiftTransitionForTests(new TimeOfDayShifted(null, "dawn",
            new DateTimeOffset(2026, 5, 23, 5, 0, 0, TimeSpan.Zero),
            Meta(new DateTimeOffset(2026, 5, 23, 5, 0, 0, TimeSpan.Zero))));

        sink.Plays.Should().BeEmpty("disabled shift never fires audio");
        svc.LastObservedShift.Should().Be("dawn", "ledger advances regardless");
    }

    // ── #712: cold-start (From == null) suppression ────────────────────

    [Fact]
    public void ColdStart_TimeOfDayShifted_does_not_play_audio_when_setting_is_OFF()
    {
        var sink = new RecordingAudioSink();
        var settings = new GandalfSettings { AlarmEnabled = true };
        var shifts = new GandalfShiftSettings();
        shifts.GetOrCreate("dawn").Enabled = true;
        shifts.RingOnCurrentShiftAtStartup.Should().BeFalse("default — pre-#709 parity");
        var bus = new TestDomainEventBus();
        var at = new DateTimeOffset(2026, 5, 23, 5, 0, 0, TimeSpan.Zero);
        using var svc = new ShiftAlarmService(Catalog, settings, shifts, sink, bus);

        svc.OnShiftTransitionForTests(new TimeOfDayShifted(From: null, To: "dawn",
            At: at, Meta(at)));

        sink.Plays.Should().BeEmpty(
            "default-off — cold-start (From == null) is silent without opt-in");
        svc.LastObservedShift.Should().Be("dawn",
            "ledger advances regardless so the next genuine transition isn't masked");
    }

    [Fact]
    public void ColdStart_TimeOfDayShifted_plays_audio_when_setting_is_ON()
    {
        var sink = new RecordingAudioSink();
        var settings = new GandalfSettings { AlarmEnabled = true };
        var shifts = new GandalfShiftSettings { RingOnCurrentShiftAtStartup = true };
        shifts.GetOrCreate("dawn").Enabled = true;
        var bus = new TestDomainEventBus();
        var at = new DateTimeOffset(2026, 5, 23, 5, 0, 0, TimeSpan.Zero);
        using var svc = new ShiftAlarmService(Catalog, settings, shifts, sink, bus);

        svc.OnShiftTransitionForTests(new TimeOfDayShifted(From: null, To: "dawn",
            At: at, Meta(at)));

        sink.Plays.Should().HaveCount(1,
            "opt-in — cold-start (From == null) fires the audio when the user has opted in");
        sink.Plays[0].CallerId.Should().Be("gandalf");
        svc.LastObservedShift.Should().Be("dawn");
    }

    [Fact]
    public void Genuine_cross_shift_transition_fires_regardless_of_cold_start_setting()
    {
        var sink = new RecordingAudioSink();
        var settings = new GandalfSettings { AlarmEnabled = true };
        var shifts = new GandalfShiftSettings();
        shifts.RingOnCurrentShiftAtStartup.Should().BeFalse();
        shifts.GetOrCreate("dawn").Enabled = true;
        var bus = new TestDomainEventBus();
        var at = new DateTimeOffset(2026, 5, 23, 5, 0, 0, TimeSpan.Zero);
        using var svc = new ShiftAlarmService(Catalog, settings, shifts, sink, bus);

        svc.OnShiftTransitionForTests(new TimeOfDayShifted(From: "night", To: "dawn",
            At: at, Meta(at)));

        sink.Plays.Should().HaveCount(1,
            "transition with a known prior shift fires regardless of the cold-start setting");
        svc.LastObservedShift.Should().Be("dawn");
    }

    [Fact]
    public void Unknown_shift_slug_is_ignored()
    {
        var sink = new RecordingAudioSink();
        var settings = new GandalfSettings { AlarmEnabled = true };
        var shifts = new GandalfShiftSettings();
        shifts.GetOrCreate("dawn").Enabled = true;
        var bus = new TestDomainEventBus();
        using var svc = new ShiftAlarmService(Catalog, settings, shifts, sink, bus);

        svc.OnShiftTransitionForTests(new TimeOfDayShifted(null, "nonsense",
            new DateTimeOffset(2026, 5, 23, 5, 0, 0, TimeSpan.Zero),
            Meta(new DateTimeOffset(2026, 5, 23, 5, 0, 0, TimeSpan.Zero))));

        sink.Plays.Should().BeEmpty();
        svc.LastObservedShift.Should().BeNull("unknown slugs never advance the ledger");
    }
}
