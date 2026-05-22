using FluentAssertions;
using Mithril.WorldSim.Player.Internal;
using Xunit;

namespace Mithril.WorldSim.Player.Tests.Internal;

public sealed class WorldClockTickFolderTests
{
    private static DateTimeOffset At(int hour, int min, int sec, int ms = 0)
        => new(2026, 1, 1, hour, min, sec, ms, TimeSpan.Zero);

    [Fact]
    public void First_tick_emits_one_CalendarTimeAdvanced_carrying_clock_state()
    {
        var folder = new WorldClockTickFolder();
        var clock = new StubClock { Now = At(12, 0, 5), Mode = WorldMode.Live };

        var emissions = folder.Apply(new Frame<WorldClockTick>(At(12, 0, 5), new WorldClockTick(At(12, 0, 5))), clock);

        emissions.Should().HaveCount(1);
        emissions[0].Should().BeOfType<CalendarTimeAdvanced>()
            .Which.Should().Be(new CalendarTimeAdvanced(At(12, 0, 5), WorldMode.Live));
    }

    [Fact]
    public void Second_tick_in_same_wall_clock_second_is_suppressed()
    {
        // Two ticks at the same second-of-day must emit only once. (Source
        // streams hammer per-line during busy stretches; consumers want one
        // tick per simulated second of advancement, not per line.)
        var folder = new WorldClockTickFolder();
        var clock = new StubClock { Now = At(12, 0, 5), Mode = WorldMode.Live };

        var first = folder.Apply(new Frame<WorldClockTick>(At(12, 0, 5, 100), new WorldClockTick(At(12, 0, 5, 100))), clock);
        var second = folder.Apply(new Frame<WorldClockTick>(At(12, 0, 5, 900), new WorldClockTick(At(12, 0, 5, 900))), clock);

        first.Should().HaveCount(1);
        second.Should().BeEmpty();
    }

    [Fact]
    public void Tick_in_next_wall_clock_second_emits_again()
    {
        var folder = new WorldClockTickFolder();
        var clock = new StubClock { Mode = WorldMode.Live };

        clock.Now = At(12, 0, 5);
        var first = folder.Apply(new Frame<WorldClockTick>(At(12, 0, 5), new WorldClockTick(At(12, 0, 5))), clock);
        clock.Now = At(12, 0, 6);
        var second = folder.Apply(new Frame<WorldClockTick>(At(12, 0, 6), new WorldClockTick(At(12, 0, 6))), clock);

        first.Should().HaveCount(1);
        second.Should().HaveCount(1);
        second[0].Should().BeOfType<CalendarTimeAdvanced>()
            .Which.Now.Should().Be(At(12, 0, 6));
    }

    [Fact]
    public void Dedup_uses_absolute_seconds_not_Second_property_wrap()
    {
        // 12:00:59 → 12:01:00 wraps .Second from 59 to 0 — a naive Second-
        // equality check would incorrectly suppress the second emission.
        // Truncating to second-resolution UTC ticks gives an unambiguous
        // scalar.
        var folder = new WorldClockTickFolder();
        var clock = new StubClock { Mode = WorldMode.Live };

        clock.Now = At(12, 0, 59);
        var first = folder.Apply(new Frame<WorldClockTick>(At(12, 0, 59), new WorldClockTick(At(12, 0, 59))), clock);
        clock.Now = At(12, 1, 0);
        var second = folder.Apply(new Frame<WorldClockTick>(At(12, 1, 0), new WorldClockTick(At(12, 1, 0))), clock);

        first.Should().HaveCount(1);
        second.Should().HaveCount(1);
    }

    [Fact]
    public void Mode_in_emitted_event_reflects_clock_mode_at_apply()
    {
        // The folder reads clock.Mode at emission so the carried Mode tracks
        // whatever the world's merger established for this dispatch.
        var folder = new WorldClockTickFolder();
        var clock = new StubClock { Mode = WorldMode.Replaying };

        clock.Now = At(12, 0, 5);
        var replayEmissions = folder.Apply(new Frame<WorldClockTick>(At(12, 0, 5), new WorldClockTick(At(12, 0, 5))), clock);

        clock.Mode = WorldMode.Live;
        clock.Now = At(12, 0, 6);
        var liveEmissions = folder.Apply(new Frame<WorldClockTick>(At(12, 0, 6), new WorldClockTick(At(12, 0, 6))), clock);

        replayEmissions.Single().Should().BeOfType<CalendarTimeAdvanced>()
            .Which.Mode.Should().Be(WorldMode.Replaying);
        liveEmissions.Single().Should().BeOfType<CalendarTimeAdvanced>()
            .Which.Mode.Should().Be(WorldMode.Live);
    }

    private sealed class StubClock : IWorldClock
    {
        public DateTimeOffset Now { get; set; }
        public long Frame { get; set; }
        public WorldMode Mode { get; set; }
    }
}
