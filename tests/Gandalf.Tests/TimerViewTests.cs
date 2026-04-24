using System.Text.Json;
using FluentAssertions;
using Gandalf.Domain;
using Xunit;

namespace Gandalf.Tests;

public class TimerViewTests
{
    private static TimerView MakeIdle(TimeSpan duration) =>
        new(new GandalfTimerDef { Duration = duration }, new TimerProgress());

    private static TimerView MakeRunning(TimeSpan duration, TimeSpan elapsed) => new(
        new GandalfTimerDef { Duration = duration },
        new TimerProgress { StartedAt = DateTimeOffset.UtcNow - elapsed });

    [Fact]
    public void Idle_view_has_no_progress()
    {
        var view = MakeIdle(TimeSpan.FromHours(1));
        view.State.Should().Be(TimerState.Idle);
        view.Remaining.Should().Be(TimeSpan.FromHours(1));
        view.Fraction.Should().Be(0.0);
    }

    [Fact]
    public void Running_view_reports_running()
    {
        var view = MakeRunning(TimeSpan.FromHours(1), TimeSpan.FromSeconds(1));
        view.State.Should().Be(TimerState.Running);
        view.Remaining.Should().BeGreaterThan(TimeSpan.Zero);
        view.Fraction.Should().BeInRange(0.0, 0.01);
    }

    [Fact]
    public void View_started_in_the_past_reports_done()
    {
        var view = MakeRunning(TimeSpan.FromMinutes(30), TimeSpan.FromHours(1));
        view.State.Should().Be(TimerState.Done);
        view.Remaining.Should().Be(TimeSpan.Zero);
        view.Fraction.Should().Be(1.0);
    }

    [Fact]
    public void Fraction_at_midpoint()
    {
        var view = MakeRunning(TimeSpan.FromHours(2), TimeSpan.FromHours(1));
        view.State.Should().Be(TimerState.Running);
        view.Fraction.Should().BeApproximately(0.5, 0.01);
    }

    [Fact]
    public void Completed_view_is_done_even_if_duration_not_elapsed()
    {
        var view = new TimerView(
            new GandalfTimerDef { Duration = TimeSpan.FromHours(1) },
            new TimerProgress
            {
                StartedAt = DateTimeOffset.UtcNow - TimeSpan.FromMinutes(5),
                CompletedAt = DateTimeOffset.UtcNow,
            });
        view.State.Should().Be(TimerState.Done);
    }

    [Fact]
    public void GroupKey_includes_region_and_map()
    {
        var view = new TimerView(
            new GandalfTimerDef { Region = "Serbule", Map = "Serbule Sewers" },
            new TimerProgress());
        view.GroupKey.Should().Be("Serbule > Serbule Sewers");
    }

    [Fact]
    public void GroupKey_region_only_when_map_blank()
    {
        var view = new TimerView(
            new GandalfTimerDef { Region = "Serbule", Map = "" },
            new TimerProgress());
        view.GroupKey.Should().Be("Serbule");
    }
}
