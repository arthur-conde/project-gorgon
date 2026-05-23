using FluentAssertions;
using Gandalf.Domain;
using Gandalf.Services;
using Mithril.Shared.Audio;
using Mithril.Shared.Game;
using Mithril.WorldSim;
using Mithril.WorldSim.Player;
using Xunit;

namespace Gandalf.Tests;

/// <summary>
/// Coverage for the event-driven <see cref="ShiftAlarmService"/>
/// (scheduler-collapse, #613). The service subscribes to PlayerWorld's
/// <see cref="TimeOfDayShift"/> domain events; the test fixture publishes
/// synthetic shifts directly via the test-only bus instead of driving the
/// real merger pipeline.
///
/// <para>The pure resolution helpers (<c>ShouldAlarm</c>,
/// <c>ResolveSoundPath</c>) are still covered as static decisions — they
/// pre-existed and have nothing to do with the scheduler collapse.</para>
/// </summary>
public class ShiftAlarmServiceTests
{
    private static readonly IShiftCatalog Catalog = new JsonShiftCatalog();

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
    public void Live_TimeOfDayShift_for_enabled_shift_plays_audio()
    {
        var sink = new RecordingAudioSink();
        var settings = new GandalfSettings { AlarmEnabled = true };
        var shifts = new GandalfShiftSettings();
        shifts.GetOrCreate("dawn").Enabled = true;
        var world = new TestPlayerWorld { Clock = { Mode = WorldMode.Live, Now = new DateTimeOffset(2026, 5, 23, 5, 0, 0, TimeSpan.Zero) } };
        using var svc = new ShiftAlarmService(Catalog, settings, shifts, sink, world);

        svc.OnShiftTransitionForTests(new TimeOfDayShift(From: null, To: "dawn",
            At: world.Clock.Now, Mode: WorldMode.Live));

        sink.Plays.Should().HaveCount(1, "Live-mode shift transition for an enabled shift fires audio");
        sink.Plays[0].CallerId.Should().Be("gandalf");
        svc.LastObservedShift.Should().Be("dawn");
    }

    [Fact]
    public void Replaying_TimeOfDayShift_advances_state_but_does_not_play_audio()
    {
        // Acceptance criterion #5 from issue #613: drain-time alarms update
        // state without playing audio. The per-shift last-observed ledger
        // advances so a subsequent Live tick for the same shift is a no-op
        // (not a retroactive ring).
        var sink = new RecordingAudioSink();
        var settings = new GandalfSettings { AlarmEnabled = true };
        var shifts = new GandalfShiftSettings();
        shifts.GetOrCreate("dawn").Enabled = true;
        var world = new TestPlayerWorld { Clock = { Mode = WorldMode.Replaying } };
        using var svc = new ShiftAlarmService(Catalog, settings, shifts, sink, world);

        svc.OnShiftTransitionForTests(new TimeOfDayShift(null, "dawn",
            new DateTimeOffset(2026, 5, 23, 5, 0, 0, TimeSpan.Zero), WorldMode.Replaying));

        sink.Plays.Should().BeEmpty(
            "principle 12 — Replaying-mode side effects (audio + window flash) are suppressed");
        svc.LastObservedShift.Should().Be("dawn",
            "state derivation is mode-agnostic so the next Live tick reuses a coherent suppression contract");
    }

    [Fact]
    public void ReplayingThenLive_same_shift_does_not_retroactively_fire()
    {
        // Missed-alarms-on-restart shape from issue #613: a shift that
        // transitioned during the drain stays as expired-but-silent; the
        // Mode → Live flip doesn't replay the already-observed shift.
        var sink = new RecordingAudioSink();
        var settings = new GandalfSettings { AlarmEnabled = true };
        var shifts = new GandalfShiftSettings();
        shifts.GetOrCreate("dawn").Enabled = true;
        var world = new TestPlayerWorld { Clock = { Mode = WorldMode.Replaying } };
        using var svc = new ShiftAlarmService(Catalog, settings, shifts, sink, world);

        // Replaying tick — drain-time, silent.
        svc.OnShiftTransitionForTests(new TimeOfDayShift(null, "dawn",
            new DateTimeOffset(2026, 5, 23, 5, 0, 0, TimeSpan.Zero), WorldMode.Replaying));
        sink.Plays.Should().BeEmpty();

        // Mode flips to Live. The composer does NOT re-emit "dawn" — it
        // emits the next bucket transition. Simulating a stray "dawn"
        // emission here would exercise the dedup ledger: bucket-level
        // dedup means even a misbehaving composer can't retroactively
        // ring the alarm for a shift the service already observed.
        world.Clock.Mode = WorldMode.Live;
        svc.OnShiftTransitionForTests(new TimeOfDayShift("night", "dawn",
            new DateTimeOffset(2026, 5, 23, 5, 0, 1, TimeSpan.Zero), WorldMode.Live));
        sink.Plays.Should().BeEmpty("dedup ledger blocks retroactive ring for an already-observed shift");
    }

    [Fact]
    public void Live_NewShift_after_Replaying_drain_fires_normally()
    {
        // Sister assertion to the missed-alarms test: a FRESH transition
        // after the Mode flip fires audibly. Drain-time replay must not
        // leave the service in a "perma-suppress" state.
        var sink = new RecordingAudioSink();
        var settings = new GandalfSettings { AlarmEnabled = true };
        var shifts = new GandalfShiftSettings();
        shifts.GetOrCreate("dawn").Enabled = true;
        shifts.GetOrCreate("morning").Enabled = true;
        var world = new TestPlayerWorld { Clock = { Mode = WorldMode.Replaying } };
        using var svc = new ShiftAlarmService(Catalog, settings, shifts, sink, world);

        // Replaying drain crosses dawn — silent.
        svc.OnShiftTransitionForTests(new TimeOfDayShift(null, "dawn",
            new DateTimeOffset(2026, 5, 23, 5, 0, 0, TimeSpan.Zero), WorldMode.Replaying));
        sink.Plays.Should().BeEmpty();

        // World catches up to Live. A new transition (morning) fires audibly.
        world.Clock.Mode = WorldMode.Live;
        svc.OnShiftTransitionForTests(new TimeOfDayShift("dawn", "morning",
            new DateTimeOffset(2026, 5, 23, 8, 0, 0, TimeSpan.Zero), WorldMode.Live));

        sink.Plays.Should().HaveCount(1, "fresh post-flip transitions fire normally");
        svc.LastObservedShift.Should().Be("morning");
    }

    [Fact]
    public void TimeOfDayShift_for_disabled_shift_advances_ledger_but_does_not_play()
    {
        var sink = new RecordingAudioSink();
        var settings = new GandalfSettings { AlarmEnabled = true };
        var shifts = new GandalfShiftSettings();
        // dawn is NOT enabled — default disabled.
        var world = new TestPlayerWorld { Clock = { Mode = WorldMode.Live } };
        using var svc = new ShiftAlarmService(Catalog, settings, shifts, sink, world);

        svc.OnShiftTransitionForTests(new TimeOfDayShift(null, "dawn",
            new DateTimeOffset(2026, 5, 23, 5, 0, 0, TimeSpan.Zero), WorldMode.Live));

        sink.Plays.Should().BeEmpty("disabled shift never fires audio");
        svc.LastObservedShift.Should().Be("dawn", "ledger advances regardless");
    }

    [Fact]
    public void Unknown_shift_slug_is_ignored()
    {
        var sink = new RecordingAudioSink();
        var settings = new GandalfSettings { AlarmEnabled = true };
        var shifts = new GandalfShiftSettings();
        shifts.GetOrCreate("dawn").Enabled = true;
        var world = new TestPlayerWorld { Clock = { Mode = WorldMode.Live } };
        using var svc = new ShiftAlarmService(Catalog, settings, shifts, sink, world);

        // A composer that emitted a slug not in the catalog must not crash
        // the service — the upstream warning is enough.
        svc.OnShiftTransitionForTests(new TimeOfDayShift(null, "nonsense",
            new DateTimeOffset(2026, 5, 23, 5, 0, 0, TimeSpan.Zero), WorldMode.Live));

        sink.Plays.Should().BeEmpty();
        svc.LastObservedShift.Should().BeNull("unknown slugs never advance the ledger");
    }
}
