using FluentAssertions;
using Mithril.Shared.Game;
using Xunit;

namespace Mithril.Shell.Tests;

/// <summary>
/// #711 — verifies the Shell chrome's "what's now / what's next?" path
/// anchors in wall-clock. <c>ShellViewModel.RefreshGameTime</c>
/// reads <see cref="IGameClock.GetCurrent"/> for the chip text and reads
/// <see cref="DateTimeOffset.UtcNow"/> as the floor for
/// <see cref="IShiftCatalog.NextTransition"/>. Both must agree on
/// the same wall-clock instant.
/// </summary>
public sealed class ShellChipCountdownTimeBaseTests
{
    [Fact]
    public void Chip_and_countdown_share_wall_clock()
    {
        var clock = new GameClock();
        var catalog = new JsonShiftCatalog();

        var floor = DateTimeOffset.UtcNow;
        var chip = clock.GetCurrent();
        var (at, shift) = catalog.NextTransition(clock, floor);

        // Chip text and countdown floor both anchor in wall-clock. Tolerated
        // jitter is one in-game minute because clock.GetCurrent() and the
        // floor read are microseconds apart and the in-game minute grain is
        // 5 real seconds.
        var floorProjection = GameClock.Project(floor);
        InGameMinuteDistance(chip, floorProjection).Should().BeLessThanOrEqualTo(1,
            "chip and countdown floor share wall-clock");

        // Countdown's target instant projects onto the named shift's StartHour.
        GameClock.Project(at).Should().Be(new GameTimeOfDay(shift.StartHour, 0),
            "NextTransition's `at` is the wall-clock instant the in-game clock " +
            "next reaches the chosen shift's StartHour");

        // Countdown duration is positive and bounded by one PG shift cycle.
        (at - floor).Should().BeGreaterThan(TimeSpan.Zero);
        (at - floor).Should().BeLessThanOrEqualTo(TimeSpan.FromMinutes(30));
    }

    private static int InGameMinuteDistance(GameTimeOfDay a, GameTimeOfDay b)
    {
        var aMin = a.Hour * 60 + a.Minute;
        var bMin = b.Hour * 60 + b.Minute;
        var raw = Math.Abs(aMin - bMin);
        return Math.Min(raw, 1440 - raw);
    }
}
