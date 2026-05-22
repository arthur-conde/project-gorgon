using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Mithril.Shared.Logging;
using Mithril.WorldSim.Player.Internal;
using Mithril.WorldSim.Player.Producers;

namespace Mithril.WorldSim.Player.DependencyInjection;

/// <summary>
/// DI registration for the Phase 0 PlayerWorld shell (issue #616, reshaped by
/// #655). Registers the world singleton, the world-clock-tick producer + its
/// owned folder, and a <see cref="BackgroundService"/> shim that calls
/// <see cref="IWorld.StartAsync"/> once the host boots. Per-folder migrations
/// (Phase 1+) wire their folder / composer / producer registrations against
/// the same world singleton — typically via dedicated DI extensions of their
/// own that resolve the world and call
/// <see cref="IWorld.RegisterFolder{T}"/> / <see cref="IWorld.RegisterComposer"/>
/// before the host starts.
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
                return world;
            })
            .AddSingleton<IPlayerWorld>(sp => sp.GetRequiredService<PlayerWorld>())
            .AddHostedService<PlayerWorldHostedService>();

        return services;
    }

    private sealed class PlayerWorldHostedService : BackgroundService
    {
        private readonly PlayerWorld _world;

        public PlayerWorldHostedService(PlayerWorld world) => _world = world;

        protected override Task ExecuteAsync(CancellationToken stoppingToken)
            => _world.StartAsync(stoppingToken);
    }
}
