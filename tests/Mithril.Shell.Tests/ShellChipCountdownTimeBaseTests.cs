using System.Runtime.CompilerServices;
using System.Threading.Channels;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Mithril.Shared.Game;
using Mithril.Shared.Logging;
using Mithril.WorldSim.Player;
using Mithril.WorldSim.Player.DependencyInjection;
using Xunit;

namespace Mithril.Shell.Tests;

/// <summary>
/// #711 — exercises the Shell chrome's "what's now / what's next?" path
/// against the post-revert DI graph. <c>ShellViewModel.RefreshGameTime</c>
/// reads <see cref="IGameClock.GetCurrent"/> for the chip text and reads
/// <see cref="DateTimeOffset.UtcNow"/> as the floor for
/// <see cref="IShiftCatalog.NextTransition"/>. Both must anchor in wall-clock
/// so the chip's in-game time and the countdown's floor reference the same
/// instant. The #709 <c>services.Replace(IGameClock)</c> made the chip read
/// from <c>PlayerWorld.Clock.Now</c> (which lags during the Replaying drain)
/// while the floor stayed wall-clock — causing the symptom this issue was
/// opened against. The revert in #711 restores wall-clock semantics for both.
/// </summary>
public sealed class ShellChipCountdownTimeBaseTests
{
    [Fact]
    public async Task Chip_and_countdown_share_wall_clock_when_PlayerWorld_clock_has_not_advanced()
    {
        // A freshly-built PlayerWorld has not yet drained its merger — its
        // clock reads DateTimeOffset.MinValue, the canonical "Replaying drain
        // hasn't started" state. If the #709 rewire returned, the chip would
        // be projecting from MinValue (i.e., a definite but wildly-wrong
        // in-game time) while the countdown floor would still be DateTimeOffset.UtcNow.
        var services = new ServiceCollection();
        services.AddSingleton<IGameClock, GameClock>();
        services.AddSingleton<IShiftCatalog>(_ => new JsonShiftCatalog());
        services.AddSingleton<IClassifiedPlayerLogStream>(new EmptyClassifiedStream());
        services.AddPlayerWorld();

        await using var sp = services.BuildServiceProvider();
        var clock = sp.GetRequiredService<IGameClock>();
        var catalog = sp.GetRequiredService<IShiftCatalog>();
        var world = sp.GetRequiredService<IPlayerWorld>();

        world.Clock.Now.Should().Be(DateTimeOffset.MinValue,
            "fresh world simulates the moment the Replaying drain begins — " +
            "rewired clock would project from this");

        // Mirror ShellViewModel.RefreshGameTime + BuildNextShiftCountdown.
        var floor = DateTimeOffset.UtcNow;
        var chip = clock.GetCurrent();
        var (at, shift) = catalog.NextTransition(clock, floor);

        // 1) Same time base — chip text and countdown floor both anchor in
        //    wall-clock. Tolerated jitter is one in-game minute because
        //    clock.GetCurrent() and the floor read are microseconds apart
        //    and the in-game minute grain is 5 real seconds.
        var floorProjection = GameClock.Project(floor);
        InGameMinuteDistance(chip, floorProjection).Should().BeLessThanOrEqualTo(1,
            "chip and countdown floor share wall-clock — the #709 rewire would " +
            "have made the chip project from PlayerWorld.Clock.Now (DateTimeOffset.MinValue " +
            "here), diverging by hundreds of in-game minutes");

        // 2) Countdown's target instant projects onto the named shift's StartHour —
        //    NextTransition's math is wall-clock-correct given a wall-clock floor
        //    + a wall-clock-anchored clock.
        GameClock.Project(at).Should().Be(new GameTimeOfDay(shift.StartHour, 0),
            "NextTransition's `at` is the wall-clock instant the in-game clock " +
            "next reaches the chosen shift's StartHour");

        // 3) Countdown duration is positive and bounded by one PG shift cycle.
        //    The published table's largest gap is 5 in-game hours = 25 real
        //    minutes; a 30-minute upper bound absorbs any jitter without
        //    masking real divergence.
        (at - floor).Should().BeGreaterThan(TimeSpan.Zero);
        (at - floor).Should().BeLessThanOrEqualTo(TimeSpan.FromMinutes(30));
    }

    /// <summary>
    /// Cyclic distance between two in-game times-of-day in minutes (0..720).
    /// 12:00 and 12:59 are 59 apart; 23:59 and 00:00 are 1 apart.
    /// </summary>
    private static int InGameMinuteDistance(GameTimeOfDay a, GameTimeOfDay b)
    {
        var aMin = a.Hour * 60 + a.Minute;
        var bMin = b.Hour * 60 + b.Minute;
        var raw = Math.Abs(aMin - bMin);
        return Math.Min(raw, 1440 - raw);
    }

    /// <summary>
    /// Empty <see cref="IClassifiedPlayerLogStream"/> so the
    /// <c>WorldClockTickProducer</c> dependency resolves. The merger is
    /// never started — only <c>IGameClock</c>, <c>IShiftCatalog</c>, and
    /// <c>IPlayerWorld</c> are pulled from the container.
    /// </summary>
    private sealed class EmptyClassifiedStream : IClassifiedPlayerLogStream
    {
        private readonly Channel<LogEnvelope<IClassifiedPlayerLogLine>> _channel
            = Channel.CreateUnbounded<LogEnvelope<IClassifiedPlayerLogLine>>(
                new UnboundedChannelOptions { SingleReader = true });

        public EmptyClassifiedStream() => _channel.Writer.TryComplete();

        public async IAsyncEnumerable<LogEnvelope<IClassifiedPlayerLogLine>>
            SubscribeWithReplayMarkerAsync([EnumeratorCancellation] CancellationToken ct)
        {
            await foreach (var e in _channel.Reader.ReadAllAsync(ct).ConfigureAwait(false))
            {
                yield return e;
            }
        }
    }
}
