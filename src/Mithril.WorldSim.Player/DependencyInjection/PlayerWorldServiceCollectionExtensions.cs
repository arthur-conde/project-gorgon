using Microsoft.Extensions.DependencyInjection;
using Mithril.Shared.Game;
using Mithril.Shared.Logging;
using Mithril.WorldSim.Player.Composers;
using Mithril.WorldSim.Player.Internal;
using Mithril.WorldSim.Player.Producers;

namespace Mithril.WorldSim.Player.DependencyInjection;

/// <summary>
/// DI registration for the Phase 0 PlayerWorld shell (issue #616, reshaped by
/// #655). Registers the world singleton + the world-clock-tick producer + its
/// owned folder. Per-folder migrations (Phase 1+) wire their folder / composer
/// / producer registrations against the same world singleton — typically via
/// dedicated DI extensions of their own that resolve the world and call
/// <see cref="IWorld.RegisterFolder{T}"/> / <see cref="IWorld.RegisterComposer"/>
/// before the host starts.
///
/// <para><b>Merger start is OUT of this extension</b> (#696 Call 2). The
/// merger drain is started trailing the entire shell composition by
/// <c>Mithril.Shell.DependencyInjection.WorldMergerStartHostedService</c>,
/// appended LAST by <c>ShellComposition.AddMithrilApp</c>. That hosted
/// service resolves <see cref="IEnumerable{IWorld}"/> and calls
/// <see cref="IWorld.StartMerger"/> on each registered world, which is why
/// this extension registers the concrete world AS <see cref="IWorld"/> as
/// well as <see cref="IPlayerWorld"/>.</para>
/// </summary>
public static class PlayerWorldServiceCollectionExtensions
{
    /// <summary>
    /// Register the PlayerWorld shell and bind it to the L1 classified pipe
    /// via the <see cref="WorldClockTickProducer"/>. Must be called after
    /// <c>AddMithrilGameServices</c> (which registers
    /// <see cref="IClassifiedPlayerLogStream"/>).
    /// </summary>
    public static IServiceCollection AddPlayerWorld(this IServiceCollection services)
    {
        services
            .AddSingleton<WorldClockTickProducer>()
            .AddSingleton<PlayerWorld>(sp =>
            {
                var world = new PlayerWorld();
                world.RegisterProducer(sp.GetRequiredService<WorldClockTickProducer>());
                world.RegisterFolder(new WorldClockTickFolder());
                // Composer: project CalendarTimeAdvanced ticks onto PG's shift
                // catalog and emit TimeOfDayShift on bucket transitions
                // (#613, scheduler-collapse migration item #12). Gandalf's
                // ShiftAlarmService subscribes to TimeOfDayShift directly.
                world.RegisterComposer(new TimeOfDayShiftComposer(
                    sp.GetRequiredService<IShiftCatalog>()));
                return world;
            })
            .AddSingleton<IPlayerWorld>(sp => sp.GetRequiredService<PlayerWorld>())
            // Also register as IWorld so the trailing
            // WorldMergerStartHostedService (#696 Call 2) can resolve every
            // registered world via IEnumerable<IWorld> without binding
            // explicitly to IPlayerWorld / IChatWorld.
            .AddSingleton<IWorld>(sp => sp.GetRequiredService<PlayerWorld>());

        // Intentionally does NOT replace the default IGameClock registration
        // from AddMithrilGameServices. IGameClock is the wall-clock-anchored
        // projection of PG in-game time-of-day — its DI-injected consumers
        // (ShellViewModel chip + countdown; TimerProgressService.ComputeFiringAt
        // for GameTimeOfDay timers) all want wall-clock semantics. Replay-
        // deterministic shift event emission flows through
        // TimeOfDayShiftComposer + the pure static GameClock.Project, with
        // the world-clock-derived CalendarTimeAdvanced.Now as input —
        // replay-deterministic by construction without involving the DI
        // IGameClock. A previous attempt (#709) to rewire IGameClock to read
        // from world.Clock.Now broke both consumer paths during the Replaying
        // drain and was reverted in #711.

        return services;
    }
}
