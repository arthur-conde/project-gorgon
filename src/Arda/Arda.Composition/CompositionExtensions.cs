using Arda.Composition.Internal;
using Arda.Dispatch;
using Arda.World.Player;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Mithril.Shared.Character;

namespace Arda.Composition;

public static class CompositionExtensions
{
    /// <summary>
    /// Register the Arda composition pipeline (L4 cross-source composers).
    /// Call after both <c>AddPlayerWorld()</c> and <c>AddChatWorld()</c>.
    /// </summary>
    /// <param name="services">Service collection.</param>
    /// <param name="charactersRootDir">Root directory for per-character state files.
    /// When <c>null</c>, persistent composers run without persistence.</param>
    /// <param name="recipeKeyResolverFactory">Optional factory that builds a
    /// <c>recipeId → InternalName</c> resolver from the service provider.
    /// When <c>null</c>, recipe IDs are formatted as <c>recipe_{id}</c>.</param>
    public static IServiceCollection AddArdaComposition(
        this IServiceCollection services,
        string? charactersRootDir = null,
        Func<IServiceProvider, Func<int, string?>>? recipeKeyResolverFactory = null)
    {
        services.AddSingleton(sp =>
        {
            var bus = sp.GetRequiredService<IDomainEventBus>();
            var loggerFactory = sp.GetService<ILoggerFactory>();

            PerCharacterStore<AccumulatorSnapshot>? store = null;
            if (!string.IsNullOrEmpty(charactersRootDir))
            {
                store = new PerCharacterStore<AccumulatorSnapshot>(
                    charactersRootDir,
                    "inventory-accumulator.json",
                    AccumulatorSnapshotJsonContext.Default.AccumulatorSnapshot,
                    logger: loggerFactory?.CreateLogger("PerCharacterStore<AccumulatorSnapshot>"));
            }

            return new InventoryComposer(bus, store,
                loggerFactory?.CreateLogger("InventoryComposer"));
        });
        services.AddSingleton<IInventoryAccumulatorState>(sp =>
            sp.GetRequiredService<InventoryComposer>());

        services.AddSingleton(sp =>
        {
            var bus = sp.GetRequiredService<IDomainEventBus>();
            var playerState = sp.GetRequiredService<IPlayerState>();
            var loggerFactory = sp.GetService<ILoggerFactory>();

            PerCharacterStore<ProgressionSnapshot>? store = null;
            if (!string.IsNullOrEmpty(charactersRootDir))
            {
                store = new PerCharacterStore<ProgressionSnapshot>(
                    charactersRootDir,
                    "player-progression.json",
                    ProgressionSnapshotJsonContext.Default.ProgressionSnapshot,
                    logger: loggerFactory?.CreateLogger("PerCharacterStore<ProgressionSnapshot>"));
            }

            var resolver = recipeKeyResolverFactory?.Invoke(sp);

            return new PlayerProgressionComposer(bus, playerState, store, resolver,
                loggerFactory?.CreateLogger("PlayerProgressionComposer"));
        });
        services.AddSingleton<IPlayerProgressionState>(sp =>
            sp.GetRequiredService<PlayerProgressionComposer>());

        services.AddSingleton(sp =>
        {
            var bus = sp.GetRequiredService<IDomainEventBus>();
            return new SessionComposer(bus);
        });
        services.AddSingleton<ISessionComposer>(sp => sp.GetRequiredService<SessionComposer>());

        return services;
    }
}
