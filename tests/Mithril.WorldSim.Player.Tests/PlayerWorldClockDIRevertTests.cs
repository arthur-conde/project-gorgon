using System.Runtime.CompilerServices;
using System.Threading.Channels;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Mithril.Shared.Game;
using Mithril.Shared.Logging;
using Mithril.WorldSim.Player.DependencyInjection;
using Xunit;

namespace Mithril.WorldSim.Player.Tests;

/// <summary>
/// #711 — locks in the revert of #709's <c>services.Replace(IGameClock)</c>.
/// The DI-resolved <see cref="IGameClock"/> after <see cref="PlayerWorldServiceCollectionExtensions.AddPlayerWorld"/>
/// must project in-game time from real wall-clock, NOT from
/// <see cref="IPlayerWorld.Clock"/>. Two consumer paths
/// (<c>ShellViewModel</c>'s chip + countdown; <c>TimerProgressService.ComputeFiringAt</c>
/// for <c>GameTimeOfDay</c>-kind timers) both want wall-clock semantics —
/// during the Replaying drain the world clock can lag wall-clock by minutes
/// without the user perceiving a different "now," so injecting a
/// world-clock-backed projection broke both.
///
/// <para>Replay-deterministic shift event emission flows through a different
/// path entirely (the <c>TimeOfDayShiftComposer</c> consumes
/// <see cref="CalendarTimeAdvanced"/> bus events and projects via the pure
/// static <see cref="GameClock.Project"/>), so the DI rewire was load-bearing
/// for nothing.</para>
/// </summary>
public sealed class PlayerWorldClockDIRevertTests
{
    [Fact]
    public async Task AddPlayerWorld_does_not_rewire_IGameClock_to_world_clock()
    {
        // Mirror AddMithrilGameServices' default IGameClock registration
        // (TimeProvider.System-backed). The post-#711 contract: AddPlayerWorld
        // must leave this registration intact. If a future change re-adds a
        // services.Replace(IGameClock, ...) inside AddPlayerWorld, this test
        // fails because the resolved clock would read from PlayerWorld.Clock.Now
        // (DateTimeOffset.MinValue on a freshly-built world) instead of from
        // wall-clock.
        var services = new ServiceCollection();
        services.AddSingleton<IGameClock, GameClock>();
        services.AddSingleton<IShiftCatalog>(_ => new JsonShiftCatalog());
        services.AddSingleton<IClassifiedPlayerLogStream>(new EmptyClassifiedStream());
        services.AddPlayerWorld();

        await using var sp = services.BuildServiceProvider();
        var clock = sp.GetRequiredService<IGameClock>();
        var world = sp.GetRequiredService<IPlayerWorld>();

        world.Clock.Now.Should().Be(DateTimeOffset.MinValue,
            "freshly-built PlayerWorld has not advanced its clock — the rewired " +
            "clock would project from this value");

        var observed = clock.GetCurrent();
        var wallClockProjection = GameClock.Project(DateTimeOffset.UtcNow);
        var worldClockProjection = GameClock.Project(world.Clock.Now);

        // Wall-clock projection and world-clock projection are separated by
        // ~hundreds of in-game hours — DateTimeOffset.MinValue is ~2000 years
        // before the GameClock anchor — so the comparison is unambiguous.
        observed.Should().Be(wallClockProjection,
            "post-#711 IGameClock projects from TimeProvider.System (wall-clock), " +
            "NOT from PlayerWorld.Clock.Now");
        observed.Should().NotBe(worldClockProjection,
            "the #709 rewire would have made GetCurrent() == Project(world.Clock.Now); " +
            "this test fails if that rewire is re-added");
    }

    /// <summary>
    /// Empty <see cref="IClassifiedPlayerLogStream"/> so
    /// <c>WorldClockTickProducer</c>'s ctor dep is satisfied. The merger is
    /// never started in these tests — we only resolve <c>IGameClock</c> and
    /// <c>IPlayerWorld</c> as DI roots.
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
