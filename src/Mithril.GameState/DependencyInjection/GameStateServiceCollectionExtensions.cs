using Mithril.GameState.Inventory;
using Mithril.GameState.Quests;
using Mithril.GameState.Quests.Parsing;
using Mithril.GameState.Sessions;
using Mithril.Shared.DependencyInjection;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace Mithril.GameState.DependencyInjection;

public static class GameStateServiceCollectionExtensions
{
    /// <summary>
    /// Register the live game-state services that mirror in-game state derived
    /// from <see cref="Mithril.Shared.Logging.IPlayerLogStream"/> /
    /// <see cref="Mithril.Shared.Logging.IChatLogStream"/>. Must be called after
    /// <c>AddMithrilGameServices()</c> and <c>AddMithrilPerCharacterStorage()</c>
    /// so the log streams, active-character service, and per-character store
    /// root are registered first.
    /// </summary>
    public static IServiceCollection AddMithrilGameState(this IServiceCollection services)
    {
        // ISessionAnchor is registered in AddMithrilGameServices as a leaf
        // (SessionAnchor). GameSessionService pushes to it on every parsed
        // banner — see SessionAnchor.cs and GameSessionService.Publish.
        services
            .AddSingleton<GameSessionService>()
            .AddSingleton<IGameSessionService>(sp => sp.GetRequiredService<GameSessionService>())
            .AddHostedService(sp => sp.GetRequiredService<GameSessionService>());

        services
            .AddSingleton<InventoryService>()
            .AddSingleton<IInventoryService>(sp => sp.GetRequiredService<InventoryService>())
            .AddHostedService(sp => sp.GetRequiredService<InventoryService>());

        services.AddSingleton<QuestJournalLoadParser>();
        services.AddSingleton<QuestAcceptedParser>();
        services.AddSingleton<QuestCompletedParser>();
        services.AddPerCharacterStore<QuestServiceState>(
            "quests.json", QuestServiceStateJsonContext.Default.QuestServiceState);
        services
            .AddSingleton<QuestService>()
            .AddSingleton<IQuestService>(sp => sp.GetRequiredService<QuestService>())
            .AddHostedService(sp => sp.GetRequiredService<QuestService>());

        return services;
    }
}
