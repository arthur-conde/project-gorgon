using FluentAssertions;
using Gandalf.Domain;
using Xunit;

namespace Gandalf.Tests;

public class ElapsedWhileAwayClassifierTests
{
    private static readonly DateTimeOffset Now = new(2026, 4, 24, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void Elapsed_while_away_when_started_before_last_active_and_finished_after()
    {
        // Started 2h ago, duration 1h30m → theoretical done at (Now - 30m).
        // LastActive 1h ago — so the timer finished 30m after the user switched away.
        var progress = new TimerProgress { StartedAt = Now - TimeSpan.FromHours(2) };
        var lastActive = Now - TimeSpan.FromHours(1);

        ElapsedWhileAwayClassifier
            .IsElapsedWhileAway(progress, TimeSpan.FromMinutes(90), lastActive, Now)
            .Should().BeTrue();
    }

    [Fact]
    public void Not_elapsed_if_still_running_past_last_active()
    {
        // Started 10m ago, duration 1h → theoretical done in ~50m (future). LastActive 5m ago.
        var progress = new TimerProgress { StartedAt = Now - TimeSpan.FromMinutes(10) };
        var lastActive = Now - TimeSpan.FromMinutes(5);

        ElapsedWhileAwayClassifier
            .IsElapsedWhileAway(progress, TimeSpan.FromHours(1), lastActive, Now)
            .Should().BeFalse("timer hasn't finished yet");
    }

    [Fact]
    public void Not_elapsed_if_started_after_last_active()
    {
        // User returned to this character, restarted a timer, then switched again.
        // Timer started after the LastActive stamp — they saw the start, not a catch-up.
        var progress = new TimerProgress { StartedAt = Now - TimeSpan.FromMinutes(10) };
        var lastActive = Now - TimeSpan.FromMinutes(30);

        ElapsedWhileAwayClassifier
            .IsElapsedWhileAway(progress, TimeSpan.FromMinutes(5), lastActive, Now)
            .Should().BeFalse("timer started after the last active session");
    }

    [Fact]
    public void Not_elapsed_if_last_active_is_null()
    {
        // First-ever session on this character: no noise.
        var progress = new TimerProgress { StartedAt = Now - TimeSpan.FromHours(5) };

        ElapsedWhileAwayClassifier
            .IsElapsedWhileAway(progress, TimeSpan.FromMinutes(5), lastActiveAt: null, Now)
            .Should().BeFalse();
    }

    [Fact]
    public void Not_elapsed_if_idle()
    {
        var progress = new TimerProgress();  // StartedAt null
        ElapsedWhileAwayClassifier
            .IsElapsedWhileAway(progress, TimeSpan.FromMinutes(5), Now - TimeSpan.FromHours(1), Now)
            .Should().BeFalse();
    }
}
