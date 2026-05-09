using System.ComponentModel;
using FluentAssertions;
using Gandalf.Domain;
using Gandalf.Services;
using Gandalf.ViewModels;
using Xunit;

namespace Gandalf.Tests;

public class TimerDisplaySchedulerTests
{
    private static readonly DateTimeOffset Anchor =
        new(2026, 5, 9, 14, 30, 0, TimeSpan.Zero);

    private static TimerItemViewModel BuildVm(
        TimeSpan duration,
        DateTimeOffset? startedAt,
        TimeProvider clock,
        DateTimeOffset? dismissedAt = null)
    {
        var catalog = new TimerCatalogEntry(
            "k", "Display", "R", duration, SourceMetadata: null);
        var progress = startedAt is null
            ? null
            : new TimerProgressEntry("k", startedAt.Value, dismissedAt);
        return new TimerItemViewModel(new TimerRow(catalog, progress) { Clock = clock });
    }

    [Fact]
    public void NextSlowTickAt_returns_null_when_no_rows_registered()
    {
        using var sched = new TimerDisplayScheduler(new ManualTime(Anchor));
        sched.NextSlowTickAt.Should().BeNull();
    }

    [Fact]
    public void NextSlowTickAt_picks_the_earliest_across_registered_rows()
    {
        var clock = new ManualTime(Anchor);
        // Two Running rows; Row A flips first, Row B is much later.
        var rowA = BuildVm(TimeSpan.FromSeconds(20), startedAt: Anchor, clock);
        var rowB = BuildVm(TimeSpan.FromHours(2), startedAt: Anchor, clock);

        using var sched = new TimerDisplayScheduler(clock);
        sched.Register(rowA);
        sched.Register(rowB);

        // Row A's NextDisplayChangeAt is its state-flip moment (Anchor + 20s),
        // which is earlier than Row B's next minute boundary.
        sched.NextSlowTickAt.Should().Be(Anchor + TimeSpan.FromSeconds(20));
    }

    [Fact]
    public void Idle_rows_do_not_contribute_to_NextSlowTickAt()
    {
        var clock = new ManualTime(Anchor);
        var idle = BuildVm(TimeSpan.FromHours(1), startedAt: null, clock);

        using var sched = new TimerDisplayScheduler(clock);
        sched.Register(idle);

        sched.NextSlowTickAt.Should().BeNull();
    }

    [Fact]
    public void AnyRunningWithProgressBar_reflects_visible_running_rows()
    {
        var clock = new ManualTime(Anchor);
        var idle = BuildVm(TimeSpan.FromHours(1), startedAt: null, clock);
        var running = BuildVm(TimeSpan.FromHours(1), startedAt: Anchor, clock);

        using var sched = new TimerDisplayScheduler(clock);
        sched.Register(idle);
        sched.AnyRunningWithProgressBar.Should().BeFalse();

        sched.Register(running);
        sched.AnyRunningWithProgressBar.Should().BeTrue();

        sched.Unregister(running);
        sched.AnyRunningWithProgressBar.Should().BeFalse();
    }

    [Fact]
    public void Slow_tick_refreshes_only_rows_whose_moment_has_arrived()
    {
        var clock = new ManualTime(Anchor);
        var earlyRow = BuildVm(TimeSpan.FromSeconds(20), startedAt: Anchor, clock);
        var lateRow = BuildVm(TimeSpan.FromHours(2), startedAt: Anchor, clock);

        using var sched = new TimerDisplayScheduler(clock);
        sched.Register(earlyRow);
        sched.Register(lateRow);

        var earlyNotifications = 0;
        var lateNotifications = 0;
        earlyRow.PropertyChanged += CountTimeDisplayChanges(() => earlyNotifications++);
        lateRow.PropertyChanged += CountTimeDisplayChanges(() => lateNotifications++);

        // Advance past earlyRow's flip but well before lateRow's.
        clock.Advance(TimeSpan.FromSeconds(25));
        sched.TickSlowForTests();

        earlyNotifications.Should().BeGreaterThan(0, "earlyRow's NextDisplayChangeAt has elapsed");
        lateNotifications.Should().Be(0, "lateRow's NextDisplayChangeAt is still in the future");
    }

    [Fact]
    public void Slow_tick_fires_RefreshRequired_when_state_flips()
    {
        var clock = new ManualTime(Anchor);
        var row = BuildVm(TimeSpan.FromSeconds(20), startedAt: Anchor, clock);

        using var sched = new TimerDisplayScheduler(clock);
        sched.Register(row);

        var refreshes = 0;
        sched.RefreshRequired += (_, _) => refreshes++;

        // Advance past state flip moment — Running → Done.
        clock.Advance(TimeSpan.FromSeconds(25));
        sched.TickSlowForTests();

        row.State.Should().Be(TimerState.Done);
        refreshes.Should().Be(1);
    }

    [Fact]
    public void Slow_tick_does_not_fire_RefreshRequired_for_text_only_changes()
    {
        // A Running row whose minute boundary rolls but state stays Running:
        // the "Xh Ym remaining" text changes but no sort/group/filter input
        // moved, so RefreshRequired should NOT fire.
        var clock = new ManualTime(Anchor);
        // 2-hour duration; minute boundary at Anchor + 1m flips before state.
        var row = BuildVm(TimeSpan.FromHours(2), startedAt: Anchor, clock);

        using var sched = new TimerDisplayScheduler(clock);
        sched.Register(row);

        var refreshes = 0;
        sched.RefreshRequired += (_, _) => refreshes++;

        clock.Advance(TimeSpan.FromMinutes(1) + TimeSpan.FromSeconds(1));
        sched.TickSlowForTests();

        row.State.Should().Be(TimerState.Running);
        refreshes.Should().Be(0, "minute roll on a Running row doesn't change sort/group/filter inputs");
    }

    [Fact]
    public void Fast_tick_refreshes_Running_rows_with_progress_bars()
    {
        var clock = new ManualTime(Anchor);
        var running = BuildVm(TimeSpan.FromHours(1), startedAt: Anchor, clock);
        var idle = BuildVm(TimeSpan.FromHours(1), startedAt: null, clock);

        using var sched = new TimerDisplayScheduler(clock);
        sched.Register(running);
        sched.Register(idle);

        var runningNotifications = 0;
        var idleNotifications = 0;
        running.PropertyChanged += CountFractionChanges(() => runningNotifications++);
        idle.PropertyChanged += CountFractionChanges(() => idleNotifications++);

        // Advance the clock so the Running row's Fraction actually moves
        // between scheduler registration and fast-tick. The per-property
        // diff in TimerItemViewModel only fires Fraction PropertyChanged
        // when the value materially changes — which is the perf win, but
        // also means tests that expect a fire must produce a real delta.
        clock.Advance(TimeSpan.FromMinutes(5));
        sched.TickFastForTests();

        runningNotifications.Should().BeGreaterThan(0);
        idleNotifications.Should().Be(0, "idle rows don't get fast-tick refreshes");
    }

    [Fact]
    public void Fast_tick_fires_RefreshRequired_when_running_row_crosses_to_done()
    {
        var clock = new ManualTime(Anchor);
        var row = BuildVm(TimeSpan.FromSeconds(20), startedAt: Anchor, clock);

        using var sched = new TimerDisplayScheduler(clock);
        sched.Register(row);

        var refreshes = 0;
        sched.RefreshRequired += (_, _) => refreshes++;

        clock.Advance(TimeSpan.FromSeconds(25));
        sched.TickFastForTests();

        row.State.Should().Be(TimerState.Done);
        refreshes.Should().Be(1);
    }

    [Fact]
    public void Disposed_scheduler_ignores_subsequent_ticks()
    {
        var clock = new ManualTime(Anchor);
        var row = BuildVm(TimeSpan.FromSeconds(20), startedAt: Anchor, clock);

        var sched = new TimerDisplayScheduler(clock);
        sched.Register(row);
        sched.Dispose();

        var refreshes = 0;
        sched.RefreshRequired += (_, _) => refreshes++;

        clock.Advance(TimeSpan.FromSeconds(25));
        sched.TickSlowForTests();
        sched.TickFastForTests();

        refreshes.Should().Be(0);
    }

    [Fact]
    public void Refresh_after_UpdateRow_picks_up_new_NextDisplayChangeAt()
    {
        // Models the binder→scheduler integration: binder applies UpdateRow,
        // then asks the scheduler to re-read the row's NextDisplayChangeAt.
        var clock = new ManualTime(Anchor);
        var row = BuildVm(TimeSpan.FromHours(2), startedAt: Anchor, clock);

        using var sched = new TimerDisplayScheduler(clock);
        sched.Register(row);

        sched.NextSlowTickAt.Should().Be(Anchor + TimeSpan.FromMinutes(1));

        // Simulate a re-stamp: update the row to a 30s timer that just started.
        var newCatalog = new TimerCatalogEntry("k", "Display", "R", TimeSpan.FromSeconds(30), null);
        var newProgress = new TimerProgressEntry("k", Anchor, DismissedAt: null);
        row.UpdateRow(new TimerRow(newCatalog, newProgress) { Clock = clock });

        sched.Refresh(row);

        // NextDisplayChangeAt should now be the state-flip moment (30s),
        // which is sooner than the next minute boundary.
        sched.NextSlowTickAt.Should().Be(Anchor + TimeSpan.FromSeconds(30));
    }

    private static PropertyChangedEventHandler CountTimeDisplayChanges(Action onMatch) =>
        (_, e) =>
        {
            if (e.PropertyName == nameof(TimerItemViewModel.TimeDisplay)) onMatch();
        };

    private static PropertyChangedEventHandler CountFractionChanges(Action onMatch) =>
        (_, e) =>
        {
            if (e.PropertyName == nameof(TimerItemViewModel.Fraction)) onMatch();
        };

    private sealed class ManualTime : TimeProvider
    {
        private DateTimeOffset _now;
        public ManualTime(DateTimeOffset utcStart) => _now = utcStart;
        public override DateTimeOffset GetUtcNow() => _now;
        public void Advance(TimeSpan delta) => _now = _now.Add(delta);
    }
}
