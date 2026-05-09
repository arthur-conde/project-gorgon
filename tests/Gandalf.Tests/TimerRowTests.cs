using FluentAssertions;
using Gandalf.Domain;
using Xunit;

namespace Gandalf.Tests;

public class TimerRowTests
{
    private static readonly DateTimeOffset Anchor =
        new(2026, 5, 9, 14, 30, 0, TimeSpan.Zero);

    private static TimerRow MakeRow(
        TimeSpan duration,
        DateTimeOffset? startedAt,
        DateTimeOffset? dismissedAt,
        TimeProvider clock)
    {
        var catalog = new TimerCatalogEntry(
            Key: "test:row",
            DisplayName: "Test",
            Region: "Test",
            Duration: duration,
            SourceMetadata: null);
        var progress = startedAt is null
            ? null
            : new TimerProgressEntry("test:row", startedAt.Value, dismissedAt);
        return new TimerRow(catalog, progress) { Clock = clock };
    }

    [Fact]
    public void Idle_with_no_progress_has_no_next_change()
    {
        var clock = new ManualTime(Anchor);
        var row = MakeRow(TimeSpan.FromHours(1), startedAt: null, dismissedAt: null, clock);

        row.State.Should().Be(TimerState.Idle);
        row.NextDisplayChangeAt.Should().BeNull();
    }

    [Fact]
    public void Dismissed_row_has_no_next_change()
    {
        var clock = new ManualTime(Anchor);
        var row = MakeRow(
            TimeSpan.FromHours(1),
            startedAt: Anchor - TimeSpan.FromMinutes(15),
            dismissedAt: Anchor - TimeSpan.FromSeconds(30),
            clock);

        row.State.Should().Be(TimerState.Idle);
        row.NextDisplayChangeAt.Should().BeNull();
    }

    [Fact]
    public void Running_returns_state_flip_when_sooner_than_next_minute()
    {
        // Anchor is on a minute boundary (14:30:00). State flips at 14:30:20.
        var clock = new ManualTime(Anchor);
        var row = MakeRow(
            TimeSpan.FromSeconds(20),
            startedAt: Anchor,
            dismissedAt: null,
            clock);

        row.State.Should().Be(TimerState.Running);
        row.NextDisplayChangeAt.Should().Be(Anchor + TimeSpan.FromSeconds(20));
    }

    [Fact]
    public void Running_returns_next_minute_when_state_flip_is_far()
    {
        // Now: 14:30:15. State flips at 15:30:15 (1 h later). Next minute at 14:31:00.
        var now = Anchor + TimeSpan.FromSeconds(15);
        var clock = new ManualTime(now);
        var row = MakeRow(
            TimeSpan.FromHours(1),
            startedAt: now,
            dismissedAt: null,
            clock);

        row.State.Should().Be(TimerState.Running);
        row.NextDisplayChangeAt.Should().Be(Anchor + TimeSpan.FromMinutes(1));
    }

    [Fact]
    public void Running_at_minute_boundary_returns_next_minute_one_minute_out()
    {
        // Now: 14:30:00 exactly. State flips at 15:30:00. Next minute at 14:31:00.
        var clock = new ManualTime(Anchor);
        var row = MakeRow(
            TimeSpan.FromHours(1),
            startedAt: Anchor,
            dismissedAt: null,
            clock);

        row.NextDisplayChangeAt.Should().Be(Anchor + TimeSpan.FromMinutes(1));
    }

    [Fact]
    public void Just_done_returns_completed_plus_sixty_seconds()
    {
        // Started at 14:29:30, duration 30s → completed at 14:30:00. Now: 14:30:10
        // (10 s into the "done!" bucket). Next change: 14:31:00 (the
        // "done 1m ago" flip at completedAt + 60 s).
        var startedAt = Anchor - TimeSpan.FromSeconds(30);
        var completedAt = Anchor;
        var now = Anchor + TimeSpan.FromSeconds(10);
        var clock = new ManualTime(now);
        var row = MakeRow(
            TimeSpan.FromSeconds(30),
            startedAt: startedAt,
            dismissedAt: null,
            clock);

        row.State.Should().Be(TimerState.Done);
        row.NextDisplayChangeAt.Should().Be(completedAt + TimeSpan.FromSeconds(60));
    }

    [Fact]
    public void Old_done_returns_next_minute_boundary()
    {
        // Completed at 14:00:00, now 14:30:15 (~30 min ago). The "done Xm ago"
        // string rolls each minute, so next change is at 14:31:00.
        var completedAt = Anchor - TimeSpan.FromMinutes(30);
        var startedAt = completedAt - TimeSpan.FromHours(1);
        var now = Anchor + TimeSpan.FromSeconds(15);
        var clock = new ManualTime(now);
        var row = MakeRow(
            TimeSpan.FromHours(1),
            startedAt: startedAt,
            dismissedAt: null,
            clock);

        row.State.Should().Be(TimerState.Done);
        row.NextDisplayChangeAt.Should().Be(Anchor + TimeSpan.FromMinutes(1));
    }

    [Fact]
    public void Done_at_exactly_sixty_seconds_uses_minute_boundary_path()
    {
        // CompletedAt + 60s == now → elapsed >= 60s → minute-boundary branch.
        var completedAt = Anchor;
        var startedAt = completedAt - TimeSpan.FromMinutes(5);
        var now = completedAt + TimeSpan.FromSeconds(60);
        var clock = new ManualTime(now);
        var row = MakeRow(
            TimeSpan.FromMinutes(5),
            startedAt: startedAt,
            dismissedAt: null,
            clock);

        row.NextDisplayChangeAt.Should().Be(Anchor + TimeSpan.FromMinutes(2));
    }

    [Fact]
    public void Advancing_clock_past_state_flip_changes_next_display()
    {
        var clock = new ManualTime(Anchor);
        var row = MakeRow(
            TimeSpan.FromSeconds(20),
            startedAt: Anchor,
            dismissedAt: null,
            clock);

        row.NextDisplayChangeAt.Should().Be(Anchor + TimeSpan.FromSeconds(20));

        clock.Advance(TimeSpan.FromSeconds(25));

        row.State.Should().Be(TimerState.Done);
        row.NextDisplayChangeAt.Should().Be(Anchor + TimeSpan.FromSeconds(20) + TimeSpan.FromSeconds(60));
    }

    private sealed class ManualTime : TimeProvider
    {
        private DateTimeOffset _now;
        public ManualTime(DateTimeOffset utcStart) => _now = utcStart;
        public override DateTimeOffset GetUtcNow() => _now;
        public void Advance(TimeSpan delta) => _now = _now.Add(delta);
    }
}
