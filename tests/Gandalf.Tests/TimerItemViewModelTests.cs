using System.ComponentModel;
using FluentAssertions;
using Gandalf.Domain;
using Gandalf.ViewModels;
using Xunit;

namespace Gandalf.Tests;

public class TimerItemViewModelTests
{
    private static readonly DateTimeOffset Anchor =
        new(2026, 5, 9, 14, 30, 0, TimeSpan.Zero);

    private static TimerCatalogEntry Entry(string key, string region, TimeSpan duration) =>
        new(key, $"Display {key}", region, duration, SourceMetadata: null);

    private static TimerRow MakeRow(
        TimerCatalogEntry catalog,
        TimerProgressEntry? progress,
        TimeProvider clock) =>
        new(catalog, progress) { Clock = clock };

    private static List<string> RecordPropertyChanges(TimerItemViewModel vm)
    {
        var changes = new List<string>();
        vm.PropertyChanged += (_, e) => { if (e.PropertyName is not null) changes.Add(e.PropertyName); };
        return changes;
    }

    [Fact]
    public void Refresh_with_no_change_fires_nothing()
    {
        var clock = new ManualTime(Anchor);
        var entry = Entry("k", "R", TimeSpan.FromHours(1));
        var vm = new TimerItemViewModel(MakeRow(entry, null, clock));

        var changes = RecordPropertyChanges(vm);
        vm.Refresh();

        changes.Should().BeEmpty("nothing in the row's projection changed since construction");
    }

    [Fact]
    public void State_transition_fires_the_State_cluster_only()
    {
        var clock = new ManualTime(Anchor);
        var entry = Entry("k", "R", TimeSpan.FromSeconds(20));
        var progress = new TimerProgressEntry("k", Anchor, DismissedAt: null);
        var vm = new TimerItemViewModel(MakeRow(entry, progress, clock));
        vm.State.Should().Be(TimerState.Running);

        var changes = RecordPropertyChanges(vm);

        // Advance past the state-flip moment.
        clock.Advance(TimeSpan.FromSeconds(25));
        vm.Refresh();

        // The full State cluster should fire — but no others (Name / GroupKey /
        // DurationLabel are catalog-derived; TimeDisplay also changes since
        // "Xs remaining" → "done!").
        var expectedClusterMembers = new[]
        {
            nameof(TimerItemViewModel.State),
            nameof(TimerItemViewModel.IsIdle),
            nameof(TimerItemViewModel.IsRunning),
            nameof(TimerItemViewModel.IsDone),
            nameof(TimerItemViewModel.StatusColor),
            nameof(TimerItemViewModel.StatusLabel),
            nameof(TimerItemViewModel.ShowStartButton),
            nameof(TimerItemViewModel.ShowRestartButton),
            nameof(TimerItemViewModel.ShowProgressBar),
        };

        changes.Should().Contain(expectedClusterMembers);
        changes.Should().NotContain(nameof(TimerItemViewModel.Name));
        changes.Should().NotContain(nameof(TimerItemViewModel.GroupKey));
        changes.Should().NotContain(nameof(TimerItemViewModel.DurationLabel));
    }

    [Fact]
    public void Minute_roll_fires_TimeDisplay_only_no_State_cluster()
    {
        // 2-hour Running row crossing the minute boundary at Anchor + 1 min.
        // State stays Running, Fraction barely moves but is past the 0.0001
        // tolerance, TimeDisplay text bucket flips.
        var clock = new ManualTime(Anchor);
        var entry = Entry("k", "R", TimeSpan.FromHours(2));
        var progress = new TimerProgressEntry("k", Anchor, DismissedAt: null);
        var vm = new TimerItemViewModel(MakeRow(entry, progress, clock));

        var changes = RecordPropertyChanges(vm);

        // Advance one minute + a sliver to ensure we cross the minute bucket
        // (FormatDuration rounds to "Xh Ym" so "2h 0m" → "1h 59m").
        clock.Advance(TimeSpan.FromMinutes(1) + TimeSpan.FromSeconds(1));
        vm.Refresh();

        changes.Should().Contain(nameof(TimerItemViewModel.TimeDisplay));
        changes.Should().Contain(nameof(TimerItemViewModel.Fraction));
        // State cluster should NOT fire — row stayed Running.
        changes.Should().NotContain(nameof(TimerItemViewModel.State));
        changes.Should().NotContain(nameof(TimerItemViewModel.IsRunning));
    }

    [Fact]
    public void TimeDisplay_uses_injected_clock_not_wall_clock()
    {
        // Regression guard for the pre-existing bug TimeDisplay's
        // "done X ago" computation read DateTimeOffset.UtcNow directly.
        // Under ManualTime, the row's CompletedAt is at Anchor + 30s but
        // wall-clock UtcNow is whatever the test process time is —
        // potentially years off from Anchor. The clock-routing fix
        // ensures the displayed string is computed against the injected
        // TimeProvider, so a Done row 5 s past completion reads "done!"
        // regardless of real-world clock skew.
        var clock = new ManualTime(Anchor);
        var entry = Entry("k", "R", TimeSpan.FromSeconds(30));
        var progress = new TimerProgressEntry("k", Anchor, DismissedAt: null);
        var vm = new TimerItemViewModel(MakeRow(entry, progress, clock));

        // Move past completion by 5 seconds — within the "done!" bucket.
        clock.Advance(TimeSpan.FromSeconds(35));
        vm.Refresh();

        vm.State.Should().Be(TimerState.Done);
        vm.TimeDisplay.Should().Be("done!", "5 s past completion is within the 60 s 'done!' window per the injected clock");
    }

    [Fact]
    public void TimeDisplay_uses_injected_clock_for_done_X_ago_bucket()
    {
        var clock = new ManualTime(Anchor);
        var entry = Entry("k", "R", TimeSpan.FromSeconds(30));
        var progress = new TimerProgressEntry("k", Anchor, DismissedAt: null);
        var vm = new TimerItemViewModel(MakeRow(entry, progress, clock));

        // Move 5 minutes past completion. Should read "done 5m 0s ago" or similar.
        clock.Advance(TimeSpan.FromSeconds(30) + TimeSpan.FromMinutes(5));
        vm.Refresh();

        vm.State.Should().Be(TimerState.Done);
        vm.TimeDisplay.Should().StartWith("done ");
        vm.TimeDisplay.Should().EndWith(" ago");
    }

    [Fact]
    public void UpdateRow_with_changed_catalog_fires_only_changed_catalog_properties()
    {
        var clock = new ManualTime(Anchor);
        var oldEntry = Entry("k", region: "Old", TimeSpan.FromHours(1));
        var vm = new TimerItemViewModel(MakeRow(oldEntry, null, clock));

        var changes = RecordPropertyChanges(vm);

        // Same Name + Duration; only Region (GroupKey) differs.
        var newEntry = oldEntry with { Region = "New" };
        vm.UpdateRow(MakeRow(newEntry, null, clock));

        changes.Should().Contain(nameof(TimerItemViewModel.GroupKey));
        changes.Should().NotContain(nameof(TimerItemViewModel.Name));
        changes.Should().NotContain(nameof(TimerItemViewModel.DurationLabel));
    }

    private sealed class ManualTime : TimeProvider
    {
        private DateTimeOffset _now;
        public ManualTime(DateTimeOffset utcStart) => _now = utcStart;
        public override DateTimeOffset GetUtcNow() => _now;
        public void Advance(TimeSpan delta) => _now = _now.Add(delta);
    }
}
