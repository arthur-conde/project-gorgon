using Arda.Composition.Internal;
using Arda.Contracts;
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
    /// <param name="serverFallbackFactory">Optional factory that builds a
    /// server-name resolver used by <see cref="Internal.SessionComposer"/> when the
    /// chat world hasn't provided a server yet. Typically wired to
    /// <c>IActiveCharacterService.ActiveServer</c>.</param>
    public static IServiceCollection AddArdaComposition(
        this IServiceCollection services,
        string? charactersRootDir = null,
        Func<IServiceProvider, Func<int, string?>>? recipeKeyResolverFactory = null,
        Func<IServiceProvider, Func<string?>>? serverFallbackFactory = null)
    {
        services.AddSingleton(sp =>
        {
            var subscriber = sp.GetRequiredService<IDomainEventSubscriber>();
            var publisher = sp.GetRequiredService<IDomainEventPublisher>();
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

            return new InventoryComposer(subscriber, publisher, store,
                sp.GetService<IGrammarBreakSignal>(),
                loggerFactory?.CreateLogger("InventoryComposer"));
        });
        services.AddSingleton<IInventoryAccumulatorState>(sp =>
            sp.GetRequiredService<InventoryComposer>());

        services.AddSingleton(sp =>
        {
            var subscriber = sp.GetRequiredService<IDomainEventSubscriber>();
            var publisher = sp.GetRequiredService<IDomainEventPublisher>();
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

            return new PlayerProgressionComposer(subscriber, publisher, playerState, store, resolver,
                sp.GetService<IGrammarBreakSignal>(),
                loggerFactory?.CreateLogger("PlayerProgressionComposer"));
        });
        services.AddSingleton<IPlayerProgressionState>(sp =>
            sp.GetRequiredService<PlayerProgressionComposer>());

        services.AddSingleton(sp =>
        {
            var subscriber = sp.GetRequiredService<IDomainEventSubscriber>();
            var publisher = sp.GetRequiredService<IDomainEventPublisher>();
            var loggerFactory = sp.GetService<ILoggerFactory>();

            PerCharacterStore<NpcStateSnapshot>? npcStore = null;
            if (!string.IsNullOrEmpty(charactersRootDir))
            {
                npcStore = new PerCharacterStore<NpcStateSnapshot>(
                    charactersRootDir,
                    "npc-state.json",
                    NpcStateSnapshotJsonContext.Default.NpcStateSnapshot,
                    legacy: new NpcStateArwenMigration(
                        charactersRootDir,
                        loggerFactory?.CreateLogger("Arda.NpcMigration")),
                    logger: loggerFactory?.CreateLogger("PerCharacterStore<NpcStateSnapshot>"));
            }

            return new NpcStateComposer(subscriber, publisher, npcStore,
                sp.GetService<IGrammarBreakSignal>(),
                loggerFactory?.CreateLogger("NpcStateComposer"));
        });
        services.AddSingleton<INpcStateTracker>(sp => sp.GetRequiredService<NpcStateComposer>());

        services.AddSingleton(sp =>
        {
            var subscriber = sp.GetRequiredService<IDomainEventSubscriber>();
            var publisher = sp.GetRequiredService<IDomainEventPublisher>();
            var fallback = serverFallbackFactory?.Invoke(sp);
            var loggerFactory = sp.GetService<ILoggerFactory>();
            return new SessionComposer(subscriber, publisher,
                loggerFactory?.CreateLogger("Arda.Session"),
                fallback);
        });
        services.AddSingleton<ISessionComposer>(sp => sp.GetRequiredService<SessionComposer>());

        return services;
    }
}
