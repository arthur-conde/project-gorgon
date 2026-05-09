using FluentAssertions;
using Gandalf.Domain;
using Gandalf.Services;
using Mithril.Shared.Game;
using Xunit;

namespace Gandalf.Tests;

/// <summary>
/// Coverage for <see cref="ShiftAlarmService"/>. Exercise the resolution
/// helpers (<c>ShouldAlarm</c> / <c>ResolveSoundPath</c>) and the scheduling
/// state (<c>NextScheduledShift</c>) without driving the WPF dispatcher pump
/// or invoking <c>AudioPlayer.Play</c> — global <c>AlarmEnabled = false</c>
/// short-circuits the audio path on tick.
/// </summary>
public class ShiftAlarmServiceTests
{
    private static readonly DateTime PgEmissaryAnchorUtc =
        new(2026, 3, 11, 1, 45, 1, 212, DateTimeKind.Utc);

    private sealed class ManualTime : TimeProvider
    {
        private DateTimeOffset _now;
        public ManualTime(DateTime utc) => _now = new DateTimeOffset(utc, TimeSpan.Zero);
        public override DateTimeOffset GetUtcNow() => _now;
        public void Advance(TimeSpan delta) => _now = _now.Add(delta);
    }

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
    public void Initial_schedule_picks_the_next_transition_after_construction()
    {
        // At anchor, in-game = 21:00 (Night). Next transition is Midnight at 0:00,
        // 3 in-game hours away = 15 real minutes.
        var time = new ManualTime(PgEmissaryAnchorUtc);
        var clock = new GameClock(time);
        var catalog = new JsonShiftCatalog();
        var settings = new GandalfSettings { AlarmEnabled = false };  // suppress audio path
        var shifts = new GandalfShiftSettings();

        using var svc = new ShiftAlarmService(clock, catalog, settings, shifts, time);
        svc.NextScheduledShift.Should().NotBeNull();
        svc.NextScheduledShift!.Slug.Should().Be("midnight");
    }

    [Fact]
    public void After_tick_reschedules_for_the_following_shift()
    {
        // At anchor, scheduledFor = midnight. Advance time past the midnight
        // transition (15 real minutes + 1 second) and force a tick. The
        // service should re-arm for Dawn (the next transition).
        var time = new ManualTime(PgEmissaryAnchorUtc);
        var clock = new GameClock(time);
        var catalog = new JsonShiftCatalog();
        var settings = new GandalfSettings { AlarmEnabled = false };
        var shifts = new GandalfShiftSettings();

        using var svc = new ShiftAlarmService(clock, catalog, settings, shifts, time);
        svc.NextScheduledShift!.Slug.Should().Be("midnight");

        time.Advance(TimeSpan.FromMinutes(15) + TimeSpan.FromSeconds(1));
        svc.TickForTests();

        // Midnight (00:00) → Dawn (05:00) is 5 in-game hours = 25 real minutes;
        // we just used 15 min + 1s so the rescheduled instant is ~25 minutes
        // out from the new "now", anchored on Dawn.
        svc.NextScheduledShift!.Slug.Should().Be("dawn");
    }

    [Fact]
    public void GetOrCreate_returns_a_disabled_default_for_an_untouched_shift()
    {
        var shifts = new GandalfShiftSettings();
        // Ensure no auto-enable lurking — the user must opt in per shift.
        foreach (var s in new JsonShiftCatalog().Shifts)
        {
            var c = shifts.GetOrCreate(s.Slug);
            c.Enabled.Should().BeFalse();
            c.SoundFilePath.Should().BeNull();
        }
    }

    [Fact]
    public void Settings_keyed_by_pre_refactor_slug_survive_catalog_swap()
    {
        // The slugs are the persistence contract between #167's shipped
        // user file (shifts.json under %LocalAppData%/Mithril/Gandalf/) and
        // whatever shift catalog ships next. Simulate a user who has already
        // toggled "dawn" + "night" before the refactor: their settings file
        // contains slugs only, and the new catalog must still resolve them.
        var preRefactor = new GandalfShiftSettings();
        preRefactor.GetOrCreate("dawn").Enabled = true;
        preRefactor.GetOrCreate("night").Enabled = true;
        preRefactor.GetOrCreate("dusk").SoundFilePath = @"C:\sounds\dusk.wav";

        var catalog = new JsonShiftCatalog();
        var liveDawn = preRefactor.GetOrCreate("dawn");
        var liveNight = preRefactor.GetOrCreate("night");
        var liveDusk = preRefactor.GetOrCreate("dusk");

        liveDawn.Enabled.Should().BeTrue("the toggle survives across the refactor");
        liveNight.Enabled.Should().BeTrue();
        liveDusk.SoundFilePath.Should().Be(@"C:\sounds\dusk.wav");

        // And every catalog slug round-trips through GetOrCreate without
        // creating a new (empty) overwrite of an existing entry.
        foreach (var s in catalog.Shifts)
        {
            var rehydrated = preRefactor.GetOrCreate(s.Slug);
            rehydrated.Should().NotBeNull();
        }
        // The two we set must remain enabled after iterating every slug.
        preRefactor.GetOrCreate("dawn").Enabled.Should().BeTrue();
        preRefactor.GetOrCreate("night").Enabled.Should().BeTrue();
    }
}
