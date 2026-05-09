using FluentAssertions;
using Mithril.Shared.Game;
using Xunit;

namespace Mithril.Shared.Tests;

public class TimeOfDayShiftsTests
{
    private static readonly DateTime PgEmissaryAnchorUtc =
        new(2026, 3, 11, 1, 45, 1, 212, DateTimeKind.Utc);

    private sealed class FixedTimeProvider : TimeProvider
    {
        private readonly DateTimeOffset _now;
        public FixedTimeProvider(DateTime utc) => _now = new DateTimeOffset(utc, TimeSpan.Zero);
        public override DateTimeOffset GetUtcNow() => _now;
    }

    [Fact]
    public void All_six_shifts_are_published_in_StartHour_order()
    {
        var slugs = TimeOfDayShifts.All.Select(s => s.Slug).ToArray();
        slugs.Should().Equal("midnight", "dawn", "morning", "afternoon", "dusk", "night");
        TimeOfDayShifts.All.Select(s => s.StartHour).Should().Equal(0, 5, 8, 12, 17, 20);
    }

    [Fact]
    public void NextTransition_at_anchor_returns_midnight_at_3_real_hours_out()
    {
        // At anchor, in-game time = 21:00 (Night). The next shift transition is
        // midnight at 0:00 in-game = 3 in-game hours later = 15 real minutes.
        var floor = new DateTimeOffset(PgEmissaryAnchorUtc, TimeSpan.Zero);
        var clock = new GameClock(new FixedTimeProvider(PgEmissaryAnchorUtc));
        var (at, shift) = TimeOfDayShifts.NextTransition(clock, floor);

        shift.Slug.Should().Be("midnight");
        (at - floor).Should().Be(TimeSpan.FromMinutes(15));
    }

    [Fact]
    public void NextTransition_just_after_a_transition_skips_to_the_following_shift()
    {
        // 1 real second after anchor: in-game ≈ 21:00:12. Next transition is
        // still midnight (we crossed nothing in 1 s). Conversely, 16 real minutes
        // after anchor → in-game = 0:12 (already past midnight) → next transition
        // is dawn at 5:00 in-game = 4h48m in-game later = 24 real minutes.
        var floor = new DateTimeOffset(PgEmissaryAnchorUtc.AddMinutes(16), TimeSpan.Zero);
        var clock = new GameClock(new FixedTimeProvider(floor.UtcDateTime));
        var (at, shift) = TimeOfDayShifts.NextTransition(clock, floor);

        shift.Slug.Should().Be("dawn");
        // 5:00 - 0:12 = 4h48m in-game = 288 in-game minutes / 12 = 24 real minutes.
        (at - floor).Should().Be(TimeSpan.FromMinutes(24));
    }

    [Theory]
    [InlineData(0, "dawn")]       // Midnight start → next is Dawn
    [InlineData(4, "dawn")]       // Mid-Midnight → next is Dawn
    [InlineData(5, "morning")]    // Dawn start → next is Morning
    [InlineData(11, "afternoon")] // End of Morning → next is Afternoon
    [InlineData(12, "dusk")]      // Afternoon start → next is Dusk
    [InlineData(16, "dusk")]      // End of Afternoon → next is Dusk
    [InlineData(17, "night")]     // Dusk start → next is Night
    [InlineData(20, "midnight")]  // Night start → next is Midnight (next day)
    [InlineData(23, "midnight")]  // End of Night → next is Midnight
    public void NextTransition_picks_the_subsequent_shift_for_each_in_game_hour(
        int gameHour, string expectedNextSlug)
    {
        // Construct a real-time floor whose in-game-hour matches the test case.
        // From anchor (21:00 in-game), we need to advance Δ real minutes such
        // that in-game = gameHour:30 (mid-shift, to avoid the 50ms epsilon edge).
        // Δ_game_minutes = ((gameHour * 60 + 30) - 21*60 + 24*60) % (24*60)
        var deltaGameMinutes = ((gameHour * 60 + 30) - 21 * 60 + 24 * 60) % (24 * 60);
        var deltaRealMinutes = deltaGameMinutes / 12.0;
        var floor = new DateTimeOffset(PgEmissaryAnchorUtc, TimeSpan.Zero)
                        .AddMinutes(deltaRealMinutes);
        var clock = new GameClock(new FixedTimeProvider(floor.UtcDateTime));

        var (_, shift) = TimeOfDayShifts.NextTransition(clock, floor);
        shift.Slug.Should().Be(expectedNextSlug);
    }
}
