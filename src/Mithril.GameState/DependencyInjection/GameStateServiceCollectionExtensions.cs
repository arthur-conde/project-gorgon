using Mithril.GameState.Inventory;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace Mithril.GameState.DependencyInjection;

public static class GameStateServiceCollectionExtensions
{
    /// <summary>
    /// Register the live game-state services that mirror in-game state derived
    /// from <see cref="Mithril.Shared.Logging.IPlayerLogStream"/> /
    /// <see cref="Mithril.Shared.Logging.IChatLogStream"/>. Must be called after
    /// <c>AddMithrilGameServices()</c> so the log streams and active-character
    /// service are registered first.
    /// </summary>
    public static IServiceCollection AddMithrilGameState(this IServiceCollection services) =>
        services
            .AddSingleton<InventoryService>()
            .AddSingleton<IInventoryService>(sp => sp.GetRequiredService<InventoryService>())
            .AddHostedService(sp => sp.GetRequiredService<InventoryService>());
}
